using ImageMagick;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Components;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using XREngine.Rendering.UI;
using XREngine.Scene;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering;

/// <summary>
/// This class is the base class for all render pipelines.
/// A render pipeline is responsible for all rendering operations to render a scene to a viewport.
/// </summary>
public sealed partial class XRRenderPipelineInstance : XRBase
{
    public XRRenderPipelineInstance()
    {
        MeshRenderCommands.SetOwnerPipeline(this);
    }

    // Persist the last render context so editor/inspector code can still attribute
    // this pipeline instance to a specific camera/viewport outside the render-scope
    // (RenderState.SceneCamera/WindowViewport are only set inside PushMainAttributes).
    public XRCamera? LastSceneCamera { get; private set; }
    public XRCamera? LastRenderingCamera { get; private set; }
    public XRViewport? LastWindowViewport { get; private set; }
    public bool? EffectiveOutputHDRThisFrame { get; private set; }
    public EAntiAliasingMode? EffectiveAntiAliasingModeThisFrame { get; private set; }
    public uint? EffectiveMsaaSampleCountThisFrame { get; private set; }
    public float? EffectiveTsrRenderScaleThisFrame { get; private set; }

    public XRRenderPipelineInstance(RenderPipeline pipeline) : this()
    {
        Pipeline = pipeline;
    }

    /// <summary>
    /// This collection contains mesh rendering commands pre-sorted for consuption by a render pass.
    /// </summary>
    public RenderCommandCollection MeshRenderCommands { get; } = new();

    public RenderResourceRegistry Resources { get; } = new();

    /// <summary>
    /// Monotonically increasing counter incremented each time physical GPU resources
    /// are invalidated (e.g., after a viewport resize). Command containers compare
    /// their last-allocated generation against this value to detect stale state and
    /// force re-allocation of per-command resources such as fullscreen quads.
    /// </summary>
    public int ResourceGeneration { get; private set; }

    // Track the last applied internal resolution scale to avoid resetting the viewport every frame.
    private float? _appliedInternalResolutionScale;
    private readonly object _renderGraphValidationLock = new();
    private readonly HashSet<int> _executedRenderGraphPassIndices = [];
    private readonly HashSet<int> _executedBranchRenderGraphPassIndices = [];
    private int _activeRenderGraphBranchDepth;

    private RenderPipeline? _pipeline;
    public RenderPipeline? Pipeline
    {
        get => _pipeline ?? SetFieldReturn(ref _pipeline, CreateDefaultRenderPipeline());
        set => SetField(ref _pipeline, value);
    }

    /// <summary>
    /// Builds a human-readable descriptor for debugging the active pipeline state.
    /// </summary>
    public string DebugDescriptor
    {
        get
        {
            RenderPipeline? pipeline = _pipeline ?? Pipeline;
            string pipelineName = pipeline?.DebugName ?? "UnknownPipeline";

            XRCamera? cam = RenderState.SceneCamera ?? RenderState.RenderingCamera ?? LastSceneCamera ?? LastRenderingCamera;
            string cameraDescription = cam is { }
                ? DescribeCamera(cam)
                : "Camera=<none>";

            XRViewport? viewport = RenderState.WindowViewport ?? LastWindowViewport;
            string viewportDescription = viewport is { }
                ? DescribeViewport(viewport)
                : "Viewport=<none>";

            string extraTags = BuildTagString(RenderState.ShadowPass, RenderState.StereoPass);

            return string.IsNullOrEmpty(extraTags)
                ? $"{pipelineName} | {cameraDescription} | {viewportDescription}"
                : $"{pipelineName} | {cameraDescription} | {viewportDescription} {extraTags}";
        }
    }

    private static string DescribeCamera(XRCamera cam)
    {
        string typeName = cam.GetType().Name;
        string? nodeName = cam.Transform.SceneNode?.Name;
        if (!string.IsNullOrWhiteSpace(nodeName))
            return $"Camera={nodeName} ({typeName})";
        return $"Camera={typeName}";
    }

    private static string DescribeViewport(XRViewport viewport)
    {
        string? baseName = viewport.Window?.Window?.Title;
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Viewport";
        return $"Viewport={baseName}#{viewport.Index} ({viewport.Width}x{viewport.Height})";
    }

    private static string BuildTagString(bool isShadow, bool isStereo)
    {
        var tags = new List<string>(2);
        if (isShadow)
            tags.Add("Shadow");
        if (isStereo)
            tags.Add("Stereo");

        return tags.Count == 0 ? string.Empty : $"[{string.Join(",", tags)}]";
    }

    private static RenderPipeline CreateDefaultRenderPipeline()
        => RuntimeRenderingHostServices.Current.CreateDefaultRenderPipeline() as RenderPipeline
            ?? throw new InvalidOperationException("RuntimeRenderingHostServices.Current did not provide a default render pipeline.");

    /// <summary>
    /// Captures all textures in the pipeline and saves them to the specified directory.
    /// This method must be called from the main thread - it's recommended to use the 'PostRender' default draw pass to call this method.
    /// </summary>
    /// <param name="exportDirPath"></param>
    public void CaptureAllTextures(string exportDirPath)
    {
        if (AbstractRenderer.Current is not OpenGLRenderer rend)
            return;

        foreach (XRTexture tex in Resources.EnumerateTextureInstances())
        {
            if (tex.APIWrappers.FirstOrDefault(x => x is IGLTexture) is not IGLTexture apiWrapper)
                continue;

            var whd = apiWrapper.WidthHeightDepth;
            BoundingRectangle region = new(0, 0, (int)whd.X, (int)whd.Y);
            for (int i = 0; i < whd.Z; i++)
            {
                void ProcessImage(MagickImage image, int layer, int channelIndex)
                {
                    string name = tex.Name ?? tex.GetDescribingName();
                    if (whd.Z > 1)
                        name += $" [Layer {layer + 1}]";
                    if (channelIndex > 0)
                        name += $" [{channelIndex + 1}]";
                    string fileName = $"{name}.png";
                    string filePath = Path.Combine(exportDirPath, fileName);
                    Utility.EnsureDirPathExists(exportDirPath);
                    image.Flip();
                    image.Write(filePath);
                }

                rend.CaptureTexture(region, ProcessImage, apiWrapper.BindingId, 0, i);
            }
        }
    }

    /// <summary>
    /// Captures all framebuffers in the pipeline and saves them to the specified directory.
    /// This method must be called from the main thread - it's recommended to use the 'PostRender' default draw pass to call this method.
    /// </summary>
    /// <param name="exportDirPath"></param>
    public void CaptureAllFBOs(string exportDirPath)
    {
        if (AbstractRenderer.Current is not OpenGLRenderer rend)
            return;

        foreach (XRFrameBuffer fbo in Resources.EnumerateFrameBufferInstances())
        {
            if (fbo.Targets is null || 
                fbo.Targets.Length == 0 || 
                fbo.APIWrappers.FirstOrDefault(x => x is GLFrameBuffer) is not GLFrameBuffer apiWrapper)
                continue;

            foreach (var (Target, Attachment, MipLevel, LayerIndex) in fbo.Targets)
            {
                void ProcessImage(MagickImage image, int index)
                {
                    string name = $"{fbo.GetDescribingName()}_{Attachment}";
                    if (index > 0)
                        name += $"_img{index + 1}";
                    if (MipLevel > 0)
                        name += $"_mip{MipLevel}";
                    if (LayerIndex >= 0)
                        name += $"_layer{LayerIndex}";
                    string fileName = $"{name}.png";
                    string filePath = Path.Combine(exportDirPath, fileName);
                    Utility.EnsureDirPathExists(exportDirPath);
                    image.Flip();
                    image.Write(filePath);
                }

                switch (Target)
                {
                    case XRTexture2D tex2D:
                        {
                            BoundingRectangle region = new(0, 0, (int)tex2D.Width, (int)tex2D.Height);
                            rend.CaptureFBOAttachment(region, true, ProcessImage, apiWrapper.BindingId, Attachment);
                        }
                        break;
                    case XRTexture2DArray tex2DArray:
                        for (int i = 0; i < tex2DArray.Depth; ++i)
                        {
                            BoundingRectangle region = new(0, 0, (int)tex2DArray.Width, (int)tex2DArray.Height);
                            rend.CaptureFBOAttachment(region, true, ProcessImage, apiWrapper.BindingId, Attachment);
                        }
                        break;
                }    

            }
        }
    }

    protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
    {
        bool change = base.OnPropertyChanging(propName, field, @new);
        if (change)
        {
            switch (propName)
            {
                case nameof(Pipeline):
                    _pipeline?.Instances.Remove(this);
                    break;
            }
        }
        return change;
    }
    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(Pipeline):
                if (_pipeline is not null)
                {
                    MeshRenderCommands.SetRenderPasses(_pipeline.PassIndicesAndSorters, _pipeline.PassMetadata);
                    InvalidMaterial = _pipeline.InvalidMaterial;
                    _pipeline.Instances.Add(this);
                }
                else
                    InvalidMaterial = null;
                DestroyCache();
                break;
        }
    }
    
    public RenderingState CollectVisibleState { get; } = new();
    public RenderingState RenderState { get; } = new();
    public XRMaterial? InvalidMaterial { get; set; }

    /// <summary>
    /// Renders the scene to the viewport or framebuffer.
    /// </summary>
    /// <param name="scene"></param>
    /// <param name="camera"></param>
    /// <param name="viewport"></param>
    /// <param name="targetFBO"></param>
    /// <param name="shadowPass"></param>
    public void Render(
        VisualScene scene,
        XRCamera? camera,
        XRCamera? stereoRightEyeCamera,
        XRViewport? viewport,
        XRFrameBuffer? targetFBO = null,
        UICanvasComponent? userInterface = null,
        bool shadowPass = false,
        bool stereoPass = false,
        XRMaterial? shadowMaterial = null,
        RenderCommandCollection? meshRenderCommandsOverride = null)
    {
        if (Pipeline is null)
        {
            Debug.Rendering("No render pipeline is set.");
            return;
        }

        // Capture the last render context for editor tooling.
        LastSceneCamera = camera;
        LastRenderingCamera = camera ?? stereoRightEyeCamera;
        LastWindowViewport = viewport;
        XRCamera? effectiveAntiAliasingCamera = camera ?? stereoRightEyeCamera;
        EAntiAliasingMode effectiveAntiAliasingMode = effectiveAntiAliasingCamera?.AntiAliasingModeOverride
            ?? Engine.EffectiveSettings.AntiAliasingMode;
        EffectiveOutputHDRThisFrame = camera?.OutputHDROverride
            ?? (camera is null ? stereoRightEyeCamera?.OutputHDROverride : null)
            ?? Engine.Rendering.Settings.OutputHDR;
        EffectiveAntiAliasingModeThisFrame = effectiveAntiAliasingMode;
        EffectiveMsaaSampleCountThisFrame = Math.Max(1u,
            effectiveAntiAliasingCamera?.MsaaSampleCountOverride ?? Engine.EffectiveSettings.MsaaSampleCount);
        EffectiveTsrRenderScaleThisFrame = effectiveAntiAliasingMode == EAntiAliasingMode.Tsr
            ? Math.Clamp(
                effectiveAntiAliasingCamera?.TsrRenderScaleOverride ?? Engine.Rendering.Settings.TsrRenderScale,
                0.5f,
                1.0f)
            : null;

        // Honor any internal resolution request from the pipeline before executing commands.
        if (viewport is not null)
        {
            float? requestedScale = Pipeline.GetRequestedInternalResolutionForCamera(effectiveAntiAliasingCamera);

            // Avoid redundant resets: only touch the viewport when the requested scale changes.
            if (requestedScale.HasValue)
            {
                float scale = Math.Clamp(requestedScale.Value, 0.25f, 1.25f);
                if (_appliedInternalResolutionScale != scale)
                {
                    _appliedInternalResolutionScale = scale;
                    viewport.SetInternalResolutionPercentage(scale, scale);
                }
            }
            else if (_appliedInternalResolutionScale.HasValue)
            {
                // Restore to native internal resolution once the request is cleared.
                _appliedInternalResolutionScale = null;
                viewport.SetInternalResolution(viewport.Width, viewport.Height, true);
            }
        }

        using (PushRenderingPipeline(this))
        {
            using (RenderState.PushMainAttributes(viewport, scene, camera, stereoRightEyeCamera, targetFBO, shadowPass, stereoPass, shadowMaterial, userInterface, meshRenderCommandsOverride ?? MeshRenderCommands))
            {
                WarnIfScreenSpaceUiHasNoRenderCommand(userInterface, viewport);
                BeginRenderGraphValidationFrame();

                if (RuntimeRenderingHostServices.Current.CurrentRenderBackend == RuntimeGraphicsApiKind.OpenGL)
                {
                    var passMetadata = Pipeline.PassMetadata;
                    if (passMetadata is { Count: > 0 })
                    {
                        // Force a topological walk so dependency cycles/missing edges are caught
                        // on the same metadata path Vulkan uses for compilation.
                        _ = RenderGraphSynchronizationPlanner.TopologicallySort(passMetadata);
                    }
                }

                Pipeline.CommandChain.Execute();

                ValidateRenderGraphExecutionAgainstMetadata();
            }
        }
    }
    
    //public void CollectVisible(VisualScene scene, XRCamera? camera, XRViewport viewport, XRFrameBuffer? targetFBO, bool shadowPass, UICanvasComponent? userInterface = null)
    //{
    //    if (Pipeline is null)
    //    {
    //        Debug.LogWarning("No render pipeline is set.");
    //        return;
    //    }
    //    using (PushRenderingPipeline(this))
    //    {
    //        using (CollectVisibleState.PushMainAttributes(viewport, scene, camera, targetFBO, shadowPass, null, userInterface))
    //        {
    //            scene.GlobalCollectVisible();
    //        }
    //    }
    //}

    public void DestroyCache()
    {
        Resources.DestroyAllPhysicalResources();
    }

    /// <summary>
    /// Destroys GPU resource instances but retains descriptor metadata so the
    /// command chain's cache-or-create commands can recreate them on the next frame
    /// without losing registry structure.
    /// </summary>
    public void InvalidatePhysicalResources()
    {
        Resources.DestroyAllPhysicalResources(retainDescriptors: true);
        ResourceGeneration++;
    }

    public void ViewportResized(Vector2 size)
    {
        //DestroyCache();
    }
    public void ViewportResized(int width, int height)
    {
        //DestroyCache();
    }
    public void InternalResolutionResized(int internalWidth, int internalHeight)
    {
        // Only destroy physical GPU resources; retain descriptor metadata so
        // cache-or-create commands detect the missing instances and recreate them
        // on the very next frame instead of requiring a full registry rebuild.
        InvalidatePhysicalResources();
    }

    public T? GetTexture<T>(string name) where T : XRTexture
    {
        if (TryGetTexture(name, out XRTexture? value))
            return value as T;
        return null;
    }

    public bool TryGetTexture(string name, out XRTexture? texture)
    {
        return Resources.TryGetTexture(name, out texture)
            || Variables.TryResolveTexture(Resources, name, out texture);
    }

    public void SetTexture(XRTexture texture, TextureResourceDescriptor? descriptor = null)
    {
        string? name = texture.Name;
        if (name is null)
        {
            Debug.Rendering("Texture name must be set before adding to the pipeline.");
            return;
        }
        Resources.BindTexture(texture, descriptor);
    }

    public XRDataBuffer? GetBuffer(string name)
    {
        if (TryGetBuffer(name, out XRDataBuffer? value))
            return value;
        return null;
    }

    public bool TryGetBuffer(string name, out XRDataBuffer? buffer)
    {
        return Resources.TryGetBuffer(name, out buffer)
            || Variables.TryResolveBuffer(Resources, name, out buffer);
    }

    public void SetBuffer(XRDataBuffer buffer, BufferResourceDescriptor? descriptor = null)
    {
        string name = buffer.AttributeName;
        if (string.IsNullOrWhiteSpace(name))
        {
            Debug.Rendering("Data buffer attribute name must be set before adding to the pipeline.");
            return;
        }

        Resources.BindBuffer(buffer, descriptor);
    }

    public T? GetFBO<T>(string name) where T : XRFrameBuffer
    {
        if (TryGetFBO(name, out XRFrameBuffer? value))
            return value as T;
        return null;
    }

    public bool TryGetFBO(string name, out XRFrameBuffer? fbo)
    {
        return Resources.TryGetFrameBuffer(name, out fbo)
            || Variables.TryResolveFrameBuffer(Resources, name, out fbo);
    }

    public void SetFBO(XRFrameBuffer fbo, FrameBufferResourceDescriptor? descriptor = null)
    {
        string? name = fbo.Name;
        if (name is null)
        {
            Debug.Rendering("FBO name must be set before adding to the pipeline.");
            return;
        }
        Resources.BindFrameBuffer(fbo, descriptor);
    }

    public XRRenderBuffer? GetRenderBuffer(string name)
    {
        if (TryGetRenderBuffer(name, out XRRenderBuffer? value))
            return value;
        return null;
    }

    public bool TryGetRenderBuffer(string name, out XRRenderBuffer? renderBuffer)
    {
        return Resources.TryGetRenderBuffer(name, out renderBuffer)
            || Variables.TryResolveRenderBuffer(Resources, name, out renderBuffer);
    }

    public void SetRenderBuffer(XRRenderBuffer renderBuffer, RenderBufferResourceDescriptor? descriptor = null)
    {
        string? name = renderBuffer.Name;
        if (name is null)
        {
            Debug.Rendering("RenderBuffer name must be set before adding to the pipeline.");
            return;
        }
        Resources.BindRenderBuffer(renderBuffer, descriptor);
    }

    private void WarnIfScreenSpaceUiHasNoRenderCommand(UICanvasComponent? userInterface, XRViewport? viewport)
    {
        if (Pipeline?.CommandChain is null || userInterface is null)
            return;

        if (userInterface.CanvasTransform.DrawSpace != ECanvasDrawSpace.Screen)
            return;

        if (ContainsScreenSpaceUiRenderCommand(Pipeline.CommandChain))
            return;

        string key = $"RenderPipeline.MissingScreenSpaceUiCommand.{GetHashCode()}";
        Debug.RenderingWarningEvery(
            key,
            TimeSpan.FromSeconds(1),
            "[RenderDiag] Screen-space UI was provided to pipeline '{0}' (viewport {1}) but no VPRC_RenderScreenSpaceUI exists in its command chain. UI will not render through the pipeline.",
            Pipeline.DebugName ?? Pipeline.GetType().Name,
            viewport?.Index ?? -1);
    }

    internal static bool ContainsScreenSpaceUiRenderCommand(ViewportRenderCommandContainer container)
    {
        foreach (var cmd in container)
        {
            if (cmd is VPRC_RenderScreenSpaceUI)
                return true;

            if (cmd is VPRC_IfElse ifElse)
            {
                if (ifElse.TrueCommands is not null && ContainsScreenSpaceUiRenderCommand(ifElse.TrueCommands))
                    return true;
                if (ifElse.FalseCommands is not null && ContainsScreenSpaceUiRenderCommand(ifElse.FalseCommands))
                    return true;
            }

            if (cmd is VPRC_Switch switchCmd)
            {
                if (switchCmd.Cases is not null)
                {
                    foreach (var caseContainer in switchCmd.Cases.Values)
                    {
                        if (ContainsScreenSpaceUiRenderCommand(caseContainer))
                            return true;
                    }
                }

                if (switchCmd.DefaultCase is not null && ContainsScreenSpaceUiRenderCommand(switchCmd.DefaultCase))
                    return true;
            }
        }

        return false;
    }

    internal void RegisterExecutedRenderGraphPass(int passIndex)
    {
        if (passIndex == int.MinValue)
            return;

        lock (_renderGraphValidationLock)
        {
            _executedRenderGraphPassIndices.Add(passIndex);
            if (_activeRenderGraphBranchDepth > 0)
                _executedBranchRenderGraphPassIndices.Add(passIndex);
        }
    }

    internal StateObject PushRenderGraphBranchScope()
    {
        lock (_renderGraphValidationLock)
            _activeRenderGraphBranchDepth++;

        return StateObject.New(() =>
        {
            lock (_renderGraphValidationLock)
            {
                if (_activeRenderGraphBranchDepth > 0)
                    _activeRenderGraphBranchDepth--;
            }
        });
    }

    private void BeginRenderGraphValidationFrame()
    {
        lock (_renderGraphValidationLock)
        {
            _executedRenderGraphPassIndices.Clear();
            _executedBranchRenderGraphPassIndices.Clear();
            _activeRenderGraphBranchDepth = 0;
        }
    }

    private void ValidateRenderGraphExecutionAgainstMetadata()
    {
        RenderPipeline? pipeline = Pipeline;
        if (pipeline is null)
            return;

        HashSet<int> executedPasses;
        HashSet<int> branchExecutedPasses;
        lock (_renderGraphValidationLock)
        {
            if (_executedRenderGraphPassIndices.Count == 0 && _executedBranchRenderGraphPassIndices.Count == 0)
                return;

            executedPasses = [.. _executedRenderGraphPassIndices];
            branchExecutedPasses = [.. _executedBranchRenderGraphPassIndices];
        }

        HashSet<int> metadataPassIndices = pipeline.PassMetadata
            .Select(m => m.PassIndex)
            .ToHashSet();

        WarnMissingPassMetadata(
            executedPasses,
            metadataPassIndices,
            "executed",
            "[RenderDiag] Pipeline '{0}' executed render-graph passes without metadata: {1}.");

        WarnMissingPassMetadata(
            branchExecutedPasses,
            metadataPassIndices,
            "branch-executed",
            "[RenderDiag] Pipeline '{0}' executed branch-selected passes without metadata: {1}.");
    }

    private void WarnMissingPassMetadata(
        HashSet<int> executedPasses,
        HashSet<int> metadataPassIndices,
        string scopeLabel,
        string messageTemplate)
    {
        if (executedPasses.Count == 0)
            return;

        int[] missing = [.. executedPasses.Where(passIndex => !metadataPassIndices.Contains(passIndex)).OrderBy(passIndex => passIndex)];
        if (missing.Length == 0)
            return;

        string missingList = string.Join(", ", missing);
        string pipelineName = Pipeline?.DebugName ?? Pipeline?.GetType().Name ?? "UnknownPipeline";

        Debug.RenderingWarningEvery(
            $"RenderGraph.MetadataCoverage.{GetHashCode()}.{scopeLabel}",
            TimeSpan.FromSeconds(1),
            messageTemplate,
            pipelineName,
            missingList);
    }
}
