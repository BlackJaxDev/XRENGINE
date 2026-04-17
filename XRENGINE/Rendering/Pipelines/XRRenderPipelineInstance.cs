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
            Debug.RenderingWarningEvery(
                $"XRRenderPipelineInstance.Render.NoPipeline.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] No render pipeline is set. Instance={0} Camera={1} Viewport={2}",
                GetHashCode(),
                camera?.Transform.SceneNode?.Name ?? "<null>",
                viewport is null ? "<null>" : $"{viewport.Index}:{viewport.Width}x{viewport.Height}");
            return;
        }

        if (Engine.PlayMode.IsTransitioning)
        {
            Debug.RenderingEvery(
                $"XRRenderPipelineInstance.Render.TransitionSuspended.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] Pipeline execution skipped during play-mode transition. Pipeline={0} State={1} Camera={2} Viewport={3}",
                Pipeline.DebugName ?? "<null>",
                Engine.PlayMode.State,
                camera?.Transform.SceneNode?.Name ?? stereoRightEyeCamera?.Transform.SceneNode?.Name ?? "<null>",
                viewport is null ? "<null>" : $"{viewport.Index}:{viewport.Width}x{viewport.Height}");
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

/*
        Debug.RenderingEvery(
            $"XRRenderPipelineInstance.FrameSettings.{GetHashCode()}.{viewport?.Index ?? -1}",
            TimeSpan.FromSeconds(1),
            "[RenderDiag] FrameSettings Pipeline={0} VP={1} Camera={2} AA={3} HDR={4} MsaaSamples={5} TsrScale={6} OutputFBO={7}",
            Pipeline.DebugName ?? Pipeline.GetType().Name,
            viewport is null ? "<null>" : $"{viewport.Index}:{viewport.Width}x{viewport.Height}",
            effectiveAntiAliasingCamera?.Transform.SceneNode?.Name ?? "<null>",
            EffectiveAntiAliasingModeThisFrame?.ToString() ?? "<null>",
            EffectiveOutputHDRThisFrame?.ToString() ?? "<null>",
            EffectiveMsaaSampleCountThisFrame?.ToString() ?? "<null>",
            EffectiveTsrRenderScaleThisFrame?.ToString() ?? "<null>",
            targetFBO?.Name ?? "<backbuffer>");
*/

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

            Engine.Rendering.PrepareVulkanUpscaleBridgeForFrame(viewport, this);
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
        LogDefaultRenderPipelineResourceDestruction("DestroyCache");
        Resources.DestroyAllPhysicalResources();
    }

    /// <summary>
    /// Destroys GPU resource instances but retains descriptor metadata so the
    /// command chain's cache-or-create commands can recreate them on the next frame
    /// without losing registry structure.
    /// </summary>
    public void InvalidatePhysicalResources()
    {
        LogDefaultRenderPipelineResourceDestruction($"InvalidatePhysicalResources (generation {ResourceGeneration} -> {ResourceGeneration + 1})");
        Resources.DestroyAllPhysicalResources(retainDescriptors: true);
        ResourceGeneration++;
    }

    internal void RemoveTextureResource(string name, string reason)
    {
        if (_pipeline is DefaultRenderPipeline pipeline
            && Resources.TryGetTexture(name, out XRTexture? texture)
            && texture is not null)
        {
            pipeline.LogTextureDestroy(this, name, texture, reason);
        }

        Resources.RemoveTexture(name);
    }

    internal void RemoveFrameBufferResource(string name, string reason)
    {
        if (_pipeline is DefaultRenderPipeline pipeline
            && Resources.TryGetFrameBuffer(name, out XRFrameBuffer? frameBuffer)
            && frameBuffer is not null)
        {
            pipeline.LogFrameBufferDestroy(this, name, frameBuffer, reason);
        }

        Resources.RemoveFrameBuffer(name);
    }

    public void ViewportResized(Vector2 size)
    {
        ViewportResized((int)size.X, (int)size.Y);
    }
    public void ViewportResized(int width, int height)
    {
        switch (_pipeline)
        {
            case DefaultRenderPipeline pipeline:
                pipeline.HandleViewportResized(this, width, height);
                break;
            case DefaultRenderPipeline2 pipeline:
                pipeline.HandleViewportResized(this, width, height);
                break;
        }
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
            Debug.RenderingWarning("Texture name must be set before adding to the pipeline.");
            return;
        }

        Resources.TryGetTexture(name, out XRTexture? existingTexture);
        if (!ReferenceEquals(existingTexture, texture) && _pipeline is DefaultRenderPipeline pipeline)
            pipeline.LogTextureBinding(this, name, texture, existingTexture);

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
            Debug.RenderingWarning("Data buffer attribute name must be set before adding to the pipeline.");
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
            Debug.RenderingWarning("FBO name must be set before adding to the pipeline.");
            return;
        }

        Resources.TryGetFrameBuffer(name, out XRFrameBuffer? existingFbo);
        if (!ReferenceEquals(existingFbo, fbo) && _pipeline is DefaultRenderPipeline pipeline)
            pipeline.LogFrameBufferBinding(this, name, fbo, existingFbo);

        Resources.BindFrameBuffer(fbo, descriptor);
    }

    private void LogDefaultRenderPipelineResourceDestruction(string reason)
    {
        if (_pipeline is not DefaultRenderPipeline pipeline)
            return;

        int liveTextureCount = 0;
        int liveFrameBufferCount = 0;
        int liveBufferCount = 0;
        int liveRenderBufferCount = 0;

        foreach (var record in Resources.TextureRecords.Values)
        {
            if (record.Instance is not XRTexture texture)
                continue;

            liveTextureCount++;
            pipeline.LogTextureDestroy(this, texture.Name ?? "<unnamed>", texture, reason);
        }

        foreach (var record in Resources.FrameBufferRecords.Values)
        {
            if (record.Instance is not XRFrameBuffer frameBuffer)
                continue;

            liveFrameBufferCount++;
            pipeline.LogFrameBufferDestroy(this, frameBuffer.Name ?? "<unnamed>", frameBuffer, reason);
        }

        foreach (var record in Resources.BufferRecords.Values)
            if (record.Instance is not null)
                liveBufferCount++;

        foreach (var record in Resources.RenderBufferRecords.Values)
            if (record.Instance is not null)
                liveRenderBufferCount++;

        if (liveTextureCount == 0
            && liveFrameBufferCount == 0
            && liveBufferCount == 0
            && liveRenderBufferCount == 0)
        {
            return;
        }

/*
        Debug.Rendering(
            "[RenderResourceDiag][{0}] Destroying physical resources for {1}. Reason={2}. textures={3} framebuffers={4} buffers={5} renderbuffers={6}",
            pipeline.DebugName,
            DebugDescriptor,
            reason,
            liveTextureCount,
            liveFrameBufferCount,
            liveBufferCount,
            liveRenderBufferCount);
*/
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
            Debug.RenderingWarning("RenderBuffer name must be set before adding to the pipeline.");
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
