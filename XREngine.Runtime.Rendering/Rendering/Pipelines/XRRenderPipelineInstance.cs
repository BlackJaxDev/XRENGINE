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
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// This class is the base class for all render pipelines.
/// A render pipeline is responsible for all rendering operations to render a scene to a viewport.
/// </summary>
public sealed partial class XRRenderPipelineInstance : XRBase, IRuntimeRenderPipelineDebugContext, IRuntimeRenderPipelineFrameContext
{
    private static int s_nextInstanceId = 0;
    private const int MaxRetiredResourceGenerations = 3;
    private const double ResizeGenerationDebounceMilliseconds = 125.0;
    private const double ResizeGenerationMaxCoalesceMilliseconds = 300.0;
    private const double IncrementalGenerationSliceMilliseconds = 2.0;
    private const int IncrementalGenerationMaxSpecsPerSlice = 4;
    private readonly RenderPipelineResourceManager _resourceManager = new();
    private readonly Queue<RenderResourceGeneration> _retiredGenerations = new();
    private readonly RenderResourceRegistry _legacyResources = new();
    private ResourceBuildContext? _resourceBuildContext;

    /// <summary>
    /// Stable, monotonically increasing identifier assigned at construction. Used by
    /// per-instance diagnostics (GPU profiler hierarchies, logging) so multiple
    /// instances of the same pipeline type are not aggregated into a single bucket.
    /// </summary>
    public int InstanceId { get; } = System.Threading.Interlocked.Increment(ref s_nextInstanceId);

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

    public RenderResourceRegistry Resources => _resourceBuildContext?.Generation.Registry
        ?? ActiveGeneration?.Registry
        ?? _legacyResources;

    public RenderResourceGeneration? ActiveGeneration { get; private set; }
    public RenderResourceGeneration? PendingGeneration { get; private set; }
    public IReadOnlyCollection<RenderResourceGeneration> RetiredGenerations => _retiredGenerations;
    internal bool SkippedResizeCatchUpThisFrame
        => _resizeCatchUpSkippedFrameId == RuntimeEngine.Rendering.State.RenderFrameId;
    private int _destroyCacheQueued;
    private int _lastDescriptorParityGeneration = -1;
    private long _pendingGenerationReadyAfterTimestamp;
    private long _pendingGenerationFirstResizeRequestTimestamp;
    private ulong _resizeCatchUpSkippedFrameId = ulong.MaxValue;

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
    internal RenderPipeline? AssignedPipeline => _pipeline;
    public RenderPipeline? Pipeline
    {
        get => _pipeline ?? SetFieldReturn(ref _pipeline, CreateDefaultRenderPipeline());
        set => SetField(ref _pipeline, value);
    }

    IRuntimeRenderPipelineHost? IRuntimeRenderPipelineFrameContext.PipelineHost => Pipeline;
    IRuntimeRenderCommandExecutionState IRuntimeRenderPipelineFrameContext.RenderState => RenderState;
    IRuntimeRenderCamera? IRuntimeRenderPipelineFrameContext.LastSceneCamera => LastSceneCamera;
    IRuntimeRenderCamera? IRuntimeRenderPipelineFrameContext.LastRenderingCamera => LastRenderingCamera;
    IRuntimeViewportHost? IRuntimeRenderPipelineFrameContext.LastWindowViewport => LastWindowViewport;

    public string DebugName
        => _pipeline?.DebugName ?? _pipeline?.GetType().Name ?? "UnknownPipeline";

    public bool IsShadowPipeline
        => _pipeline is XREngine.Components.Lights.ShadowRenderPipeline ||
           _pipeline?.IsShadowPass == true;

    /// <summary>
    /// Profiler-stable identifier for this pipeline instance. Combines <see cref="DebugName"/>
    /// with <see cref="InstanceId"/> so the GPU profiler keeps separate graphs/hierarchies
    /// per instantiation rather than collapsing all instances of the same pipeline type into
    /// a single bucket (which artificially inflates per-frame totals).
    /// </summary>
    public string ProfilerKey => $"{DebugName}#{InstanceId}";

    internal ResourceBuildContext? CurrentResourceBuildContext => _resourceBuildContext;
    internal uint? ResourceInternalWidth
        => _resourceBuildContext?.Key.InternalWidth
            ?? ActiveGeneration?.Key.InternalWidth;
    internal uint? ResourceInternalHeight
        => _resourceBuildContext?.Key.InternalHeight
            ?? ActiveGeneration?.Key.InternalHeight;
    internal uint? ResourceDisplayWidth
        => _resourceBuildContext?.Key.DisplayWidth
            ?? ActiveGeneration?.Key.DisplayWidth;
    internal uint? ResourceDisplayHeight
        => _resourceBuildContext?.Key.DisplayHeight
            ?? ActiveGeneration?.Key.DisplayHeight;

    internal sealed class ResourceBuildContext
    {
        public ResourceBuildContext(RenderResourceGeneration generation, int managedThreadId)
        {
            Generation = generation;
            ManagedThreadId = managedThreadId;
        }

        public RenderResourceGeneration Generation { get; }
        public int ManagedThreadId { get; }
        public ResourceGenerationKey Key => Generation.Key;
    }

    internal IDisposable PushResourceBuildContext(RenderResourceGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);

        int managedThreadId = Environment.CurrentManagedThreadId;
        if (_resourceBuildContext is not null)
        {
            string message = _resourceBuildContext.ManagedThreadId == managedThreadId
                ? $"Nested render-resource build contexts are not supported. Active={_resourceBuildContext.Generation.Key} New={generation.Key}"
                : $"Cross-thread render-resource build context detected. ActiveThread={_resourceBuildContext.ManagedThreadId} CurrentThread={managedThreadId}";
            throw new InvalidOperationException(message);
        }

        _resourceBuildContext = new ResourceBuildContext(generation, managedThreadId);
        return StateObject.New(() =>
        {
            if (_resourceBuildContext?.Generation == generation)
                _resourceBuildContext = null;
        });
    }

    /// <summary>
    /// Builds a human-readable descriptor for debugging the active pipeline state.
    /// </summary>
    public string DebugDescriptor
    {
        get
        {
            RenderPipeline? pipeline = _pipeline ?? Pipeline;
            string pipelineName = pipeline?.DebugName ?? DebugName;

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
                DestroyCacheIfResourcesExist();
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
        IRuntimeScreenSpaceUserInterface? userInterface = null,
        bool shadowPass = false,
        bool stereoPass = false,
        XRMaterial? shadowMaterial = null,
        RenderCommandCollection? meshRenderCommandsOverride = null)
    {
        IRuntimeRenderingHostServices hostServices = RuntimeRenderingHostServices.Current;

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

        if (hostServices.IsPlayModeTransitioning)
        {
            Debug.RenderingEvery(
                $"XRRenderPipelineInstance.Render.TransitionSuspended.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] Pipeline execution skipped during play-mode transition. Pipeline={0} State={1} Camera={2} Viewport={3}",
                Pipeline.DebugName ?? "<null>",
                hostServices.PlayModeStateName,
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
            ?? hostServices.DefaultAntiAliasingMode;
        EffectiveOutputHDRThisFrame = camera?.OutputHDROverride
            ?? (camera is null ? stereoRightEyeCamera?.OutputHDROverride : null)
            ?? hostServices.DefaultOutputHDR;
        EffectiveAntiAliasingModeThisFrame = effectiveAntiAliasingMode;
        EffectiveMsaaSampleCountThisFrame = Math.Max(1u,
            effectiveAntiAliasingCamera?.MsaaSampleCountOverride ?? hostServices.DefaultMsaaSampleCount);
        EffectiveTsrRenderScaleThisFrame = effectiveAntiAliasingMode == EAntiAliasingMode.Tsr
            ? Math.Clamp(
                effectiveAntiAliasingCamera?.TsrRenderScaleOverride ?? hostServices.DefaultTsrRenderScale,
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

            if (!RuntimeEngine.Rendering.State.IsSceneCapturePass && !RuntimeEngine.Rendering.State.IsLightProbePass)
                hostServices.PrepareUpscaleBridgeForFrame(viewport, this);
        }

        using (hostServices.PushRenderingPipeline(this))
        {
            using (RenderState.PushMainAttributes(viewport, scene, camera, stereoRightEyeCamera, targetFBO, shadowPass, stereoPass, shadowMaterial, userInterface, meshRenderCommandsOverride ?? MeshRenderCommands))
            {
                WarnIfScreenSpaceUiHasNoRenderCommand(userInterface, viewport);
                if (!EnsureResourceGenerationForCurrentFrame(viewport))
                {
                    _resizeCatchUpSkippedFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
                    Debug.RenderingEvery(
                        $"RenderResources.FrameSkippedForResizeCatchUp.{ProfilerKey}",
                        TimeSpan.FromMilliseconds(250),
                        "[RenderResources] Skipping command chain until resize resources catch up. Pipeline={0} Active={1} Pending={2} Viewport={3}",
                        ProfilerKey,
                        ActiveGeneration?.Key.ToString() ?? "<none>",
                        PendingGeneration?.Key.ToString() ?? "<none>",
                        viewport is null ? "<null>" : $"{viewport.Index}:{viewport.Width}x{viewport.Height}/{viewport.InternalWidth}x{viewport.InternalHeight}");
                    return;
                }
                _resizeCatchUpSkippedFrameId = ulong.MaxValue;
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

                ValidateActiveGenerationDescriptorParity();
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

    private bool EnqueueResourceMutationIfOffRenderThread(
        Action action,
        string reason,
        RenderThreadJobKind renderThreadKind = RenderThreadJobKind.RenderPipelineResource)
    {
        if (RuntimeEngine.IsRenderThread)
            return false;

        RuntimeEngine.EnqueueRenderThreadTask(action, reason, renderThreadKind);
        return true;
    }

    private bool EnsureResourceGenerationForCurrentFrame(XRViewport? viewport)
    {
        if (Pipeline is null || viewport is null)
            return true;

        if (viewport.Window?.IsInteractiveResizeInProgress == true && ActiveGeneration is not null)
        {
            DiscardPendingGeneration("InteractiveResize");
            return true;
        }

        ResourceGenerationKey key = BuildResourceGenerationKey(
            Math.Max(1, viewport.Width),
            Math.Max(1, viewport.Height),
            Math.Max(1, viewport.InternalWidth),
            Math.Max(1, viewport.InternalHeight));

        if (ActiveGeneration is null && PendingGeneration is null)
            RequestResourceGeneration(key, "Initial");
        else if (ActiveGeneration is not null && ActiveGeneration.Key != key && PendingGeneration?.Key != key)
            RequestResourceGeneration(key, "FrameProfileChanged");

        bool resizeOnlyMismatch =
            ActiveGeneration is not null &&
            ActiveGeneration.Key != key &&
            IsResizeOnlyGenerationDelta(ActiveGeneration.Key, key);

        if (resizeOnlyMismatch && PendingGeneration?.Key == key)
        {
            TryPreparePendingGeneration(
                "FramePrepareResizeCatchUp",
                forceDue: true,
                catchUpMaxDuration: TimeSpan.FromMilliseconds(8.0),
                catchUpMaxSpecsPerSlice: 16);
        }
        else
        {
            TryPreparePendingGeneration("FramePrepare");
        }

        if (ActiveGeneration is null)
        {
            // Pipelines without a declared resource layout still use the legacy
            // command path. Do not starve UI/shadow/specialized passes just
            // because they have no active managed generation.
            return PendingGeneration is null;
        }

        if (ActiveGeneration.Key == key)
            return true;

        return !IsResizeOnlyGenerationDelta(ActiveGeneration.Key, key);
    }

    private static bool IsResizeOnlyGenerationDelta(ResourceGenerationKey oldKey, ResourceGenerationKey newKey)
        => string.Equals(oldKey.PipelineName, newKey.PipelineName, StringComparison.Ordinal) &&
           oldKey.OutputHDR == newKey.OutputHDR &&
           oldKey.AntiAliasingMode == newKey.AntiAliasingMode &&
           oldKey.MsaaSampleCount == newKey.MsaaSampleCount &&
           oldKey.Stereo == newKey.Stereo &&
           oldKey.FeatureMask == newKey.FeatureMask &&
           oldKey.ReservedViewCount == newKey.ReservedViewCount &&
           oldKey.ReservedEyeIndex == newKey.ReservedEyeIndex &&
           (oldKey.DisplayWidth != newKey.DisplayWidth ||
            oldKey.DisplayHeight != newKey.DisplayHeight ||
            oldKey.InternalWidth != newKey.InternalWidth ||
            oldKey.InternalHeight != newKey.InternalHeight);

    internal bool RequestResourceGeneration(
        int displayWidth,
        int displayHeight,
        int internalWidth,
        int internalHeight,
        string reason)
        => RequestResourceGeneration(
            BuildResourceGenerationKey(displayWidth, displayHeight, internalWidth, internalHeight),
            reason);

    private bool RequestResourceGeneration(ResourceGenerationKey key, string reason)
    {
        RenderPipeline? pipeline = Pipeline;
        if (pipeline is null)
            return false;

        if (ActiveGeneration?.Key == key)
        {
            DiscardPendingGeneration($"Active generation already matches request: {reason}");
            return true;
        }

        if (PendingGeneration?.Key == key)
            return true;

        RenderPipelineResourceLayout layout;
        try
        {
            layout = pipeline.BuildResourceLayout(key.ToProfile());
        }
        catch (Exception ex)
        {
            Debug.RenderingWarning(
                "[RenderResources] Failed to describe pending generation. Pipeline={0} Target={1} Reason={2}",
                ProfilerKey,
                key,
                ex.Message);
            return false;
        }

        if (layout.OrderedSpecs.Count == 0)
            return false;

        if (PendingGeneration is not null)
        {
            Debug.Rendering(
                "[RenderResources] Pending generation superseded. Pipeline={0} Reason={1} OldPending={2} Target={3} Delta={4}",
                ProfilerKey,
                reason,
                PendingGeneration.Key,
                key,
                DescribeResourceGenerationKeyDelta(PendingGeneration.Key, key));
            PendingGeneration.MarkSuperseded(reason);
            PendingGeneration.Dispose();
        }

        PendingGeneration = new RenderResourceGeneration(key, layout);
        ConfigurePendingGenerationDebounce(key, reason);
        Debug.Rendering(
            "[RenderResources] Pending generation requested. Pipeline={0} Reason={1} Active={2} Target={3} Delta={4} Resources={5} DebounceMs={6:F0}",
            ProfilerKey,
            reason,
            ActiveGeneration?.Key.ToString() ?? "<none>",
            key,
            DescribeResourceGenerationKeyDelta(ActiveGeneration?.Key, key),
            layout.OrderedSpecs.Count,
            PendingGenerationDebounceRemainingMilliseconds());
        return true;
    }

    private void ConfigurePendingGenerationDebounce(ResourceGenerationKey key, string reason)
    {
        if (!ShouldDebouncePendingGeneration(key, reason))
        {
            _pendingGenerationReadyAfterTimestamp = 0;
            _pendingGenerationFirstResizeRequestTimestamp = 0;
            return;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_pendingGenerationFirstResizeRequestTimestamp == 0)
            _pendingGenerationFirstResizeRequestTimestamp = now;

        long debounceTicks = StopwatchTicksFromMilliseconds(ResizeGenerationDebounceMilliseconds);
        long maxTicks = StopwatchTicksFromMilliseconds(ResizeGenerationMaxCoalesceMilliseconds);
        long debounceUntil = now + debounceTicks;
        long maxUntil = _pendingGenerationFirstResizeRequestTimestamp + maxTicks;
        _pendingGenerationReadyAfterTimestamp = Math.Min(debounceUntil, maxUntil);
    }

    private bool ShouldDebouncePendingGeneration(ResourceGenerationKey key, string reason)
    {
        if (ActiveGeneration is null)
            return false;

        if (reason.Contains("Resize", StringComparison.OrdinalIgnoreCase))
            return true;

        ResourceGenerationKey activeKey = ActiveGeneration.Key;
        return activeKey.DisplayWidth != key.DisplayWidth
            || activeKey.DisplayHeight != key.DisplayHeight
            || activeKey.InternalWidth != key.InternalWidth
            || activeKey.InternalHeight != key.InternalHeight;
    }

    private static string DescribeResourceGenerationKeyDelta(ResourceGenerationKey? oldKey, ResourceGenerationKey newKey)
    {
        if (!oldKey.HasValue)
            return "initial";

        ResourceGenerationKey old = oldKey.Value;
        List<string> deltas = [];

        if (!string.Equals(old.PipelineName, newKey.PipelineName, StringComparison.Ordinal))
            deltas.Add($"pipeline:{old.PipelineName}->{newKey.PipelineName}");
        if (old.DisplayWidth != newKey.DisplayWidth || old.DisplayHeight != newKey.DisplayHeight)
            deltas.Add($"display:{old.DisplayWidth}x{old.DisplayHeight}->{newKey.DisplayWidth}x{newKey.DisplayHeight}");
        if (old.InternalWidth != newKey.InternalWidth || old.InternalHeight != newKey.InternalHeight)
            deltas.Add($"internal:{old.InternalWidth}x{old.InternalHeight}->{newKey.InternalWidth}x{newKey.InternalHeight}");
        if (old.OutputHDR != newKey.OutputHDR)
            deltas.Add($"hdr:{old.OutputHDR}->{newKey.OutputHDR}");
        if (old.AntiAliasingMode != newKey.AntiAliasingMode)
            deltas.Add($"aa:{old.AntiAliasingMode}->{newKey.AntiAliasingMode}");
        if (old.MsaaSampleCount != newKey.MsaaSampleCount)
            deltas.Add($"msaa:{old.MsaaSampleCount}->{newKey.MsaaSampleCount}");
        if (old.Stereo != newKey.Stereo)
            deltas.Add($"stereo:{old.Stereo}->{newKey.Stereo}");
        if (old.FeatureMask != newKey.FeatureMask)
            deltas.Add($"features:0x{old.FeatureMask:X}->0x{newKey.FeatureMask:X}");
        if (old.ReservedViewCount != newKey.ReservedViewCount)
            deltas.Add($"views:{old.ReservedViewCount}->{newKey.ReservedViewCount}");
        if (old.ReservedEyeIndex != newKey.ReservedEyeIndex)
            deltas.Add($"eye:{old.ReservedEyeIndex}->{newKey.ReservedEyeIndex}");

        return deltas.Count == 0 ? "none" : string.Join(", ", deltas);
    }

    private double PendingGenerationDebounceRemainingMilliseconds()
    {
        long due = _pendingGenerationReadyAfterTimestamp;
        if (due == 0)
            return 0.0;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now >= due)
            return 0.0;

        return (due - now) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static long StopwatchTicksFromMilliseconds(double milliseconds)
        => (long)(milliseconds * System.Diagnostics.Stopwatch.Frequency / 1000.0);

    private ResourceGenerationKey BuildResourceGenerationKey(
        int displayWidth,
        int displayHeight,
        int internalWidth,
        int internalHeight)
    {
        bool stereo = Pipeline switch
        {
            DefaultRenderPipeline defaultPipeline => defaultPipeline.Stereo,
            DefaultRenderPipeline2 defaultPipeline2 => defaultPipeline2.Stereo,
            _ => RenderState.StereoPass
        };

        bool outputHdr = EffectiveOutputHDRThisFrame
            ?? LastSceneCamera?.OutputHDROverride
            ?? LastRenderingCamera?.OutputHDROverride
            ?? RuntimeRenderingHostServices.Current.DefaultOutputHDR;

        EAntiAliasingMode antiAliasingMode = EffectiveAntiAliasingModeThisFrame
            ?? LastSceneCamera?.AntiAliasingModeOverride
            ?? LastRenderingCamera?.AntiAliasingModeOverride
            ?? RuntimeRenderingHostServices.Current.DefaultAntiAliasingMode;

        uint msaaSamples = Math.Max(1u,
            EffectiveMsaaSampleCountThisFrame
                ?? LastSceneCamera?.MsaaSampleCountOverride
                ?? LastRenderingCamera?.MsaaSampleCountOverride
                ?? RuntimeRenderingHostServices.Current.DefaultMsaaSampleCount);

        ulong featureMask = Pipeline switch
        {
            DefaultRenderPipeline defaultPipeline => defaultPipeline.BuildResourceFeatureMaskForGenerationKey(),
            _ => 0UL
        };

        return new ResourceGenerationKey(
            Pipeline?.DebugName ?? DebugName,
            (uint)Math.Max(1, displayWidth),
            (uint)Math.Max(1, displayHeight),
            (uint)Math.Max(1, internalWidth),
            (uint)Math.Max(1, internalHeight),
            outputHdr,
            antiAliasingMode,
            msaaSamples,
            stereo,
            featureMask);
    }

    private bool TryPreparePendingGeneration(string reason)
        => TryPreparePendingGeneration(
            reason,
            forceDue: false,
            catchUpMaxDuration: TimeSpan.Zero,
            catchUpMaxSpecsPerSlice: 0);

    private bool TryPreparePendingGeneration(
        string reason,
        bool forceDue,
        TimeSpan catchUpMaxDuration,
        int catchUpMaxSpecsPerSlice)
    {
        RenderResourceGeneration? pending = PendingGeneration;
        if (pending is null)
            return false;

        if (!forceDue && !IsPendingGenerationDue())
        {
            Debug.RenderingEvery(
                $"RenderResources.PendingDebounce.{ProfilerKey}",
                TimeSpan.FromMilliseconds(250),
                "[RenderResources] Pending generation debounce. Pipeline={0} Reason={1} RemainingMs={2:F0} Target={3}",
                ProfilerKey,
                reason,
                PendingGenerationDebounceRemainingMilliseconds(),
                pending.Key);
            return false;
        }

        if (forceDue)
            ClearPendingGenerationDebounce();

        if (pending.IsReady)
        {
            CommitPendingGeneration(reason);
            return true;
        }

        bool immediate = ActiveGeneration is null;
        TimeSpan maxDuration = immediate
            ? TimeSpan.MaxValue
            : forceDue
                ? catchUpMaxDuration
                : TimeSpan.FromMilliseconds(IncrementalGenerationSliceMilliseconds);
        int maxSpecsPerSlice = immediate
            ? int.MaxValue
            : forceDue
                ? catchUpMaxSpecsPerSlice
                : IncrementalGenerationMaxSpecsPerSlice;

        if (!_resourceManager.MaterializeIncremental(this, pending, maxDuration, maxSpecsPerSlice, out bool completed))
        {
            PendingGeneration = null;
            ClearPendingGenerationDebounce();
            pending.Dispose();
            return false;
        }

        if (!completed)
        {
            Debug.RenderingEvery(
                $"RenderResources.PendingIncremental.{ProfilerKey}",
                TimeSpan.FromMilliseconds(250),
                "[RenderResources] Pending generation incremental build. Pipeline={0} Reason={1} Progress={2}/{3} Target={4}",
                ProfilerKey,
                reason,
                pending.MaterializedSpecCount,
                pending.Layout.OrderedSpecs.Count,
                pending.Key);
            return false;
        }

        CommitPendingGeneration(reason);
        return true;
    }

    private bool IsPendingGenerationDue()
    {
        long due = _pendingGenerationReadyAfterTimestamp;
        return due == 0 || System.Diagnostics.Stopwatch.GetTimestamp() >= due;
    }

    private void ClearPendingGenerationDebounce()
    {
        _pendingGenerationReadyAfterTimestamp = 0;
        _pendingGenerationFirstResizeRequestTimestamp = 0;
    }

    private void DiscardPendingGeneration(string reason)
    {
        RenderResourceGeneration? pending = PendingGeneration;
        if (pending is null)
            return;

        Debug.Rendering(
            "[RenderResources] Pending generation discarded. Pipeline={0} Reason={1} Pending={2} Active={3}",
            ProfilerKey,
            reason,
            pending.Key,
            ActiveGeneration?.Key.ToString() ?? "<none>");

        PendingGeneration = null;
        ClearPendingGenerationDebounce();
        pending.MarkSuperseded(reason);
        pending.Dispose();
    }

    private void CommitPendingGeneration(string reason)
    {
        RenderResourceGeneration? pending = PendingGeneration;
        if (pending is null || !pending.IsReady)
            return;

        if (TryBuildCurrentViewportGenerationKey(out ResourceGenerationKey currentKey) &&
            pending.Key != currentKey)
        {
            Debug.RenderingWarning(
                "[RenderResources] Pending generation is stale at commit. Pipeline={0} Reason={1} Pending={2} Current={3} Delta={4}",
                ProfilerKey,
                reason,
                pending.Key,
                currentKey,
                DescribeResourceGenerationKeyDelta(pending.Key, currentKey));
            DiscardPendingGeneration($"StaleCommit:{reason}");
            return;
        }

        RenderResourceGeneration? old = ActiveGeneration;
        ActiveGeneration = pending;
        PendingGeneration = null;
        ClearPendingGenerationDebounce();
        ActiveGeneration.MarkActive(reason);
        ResourceGeneration++;

        if (old is not null)
            RetireGeneration(old, $"Committed replacement generation: {reason}");
        else
            _legacyResources.DestroyAllPhysicalResources();

        NotifyRenderResourcesChanged();

        Debug.Rendering(
            "[RenderResources] Pending generation committed. Pipeline={0} Reason={1} Previous={2} Active={3} Delta={4} Textures={5} FBOs={6} Buffers={7} RenderBuffers={8} BuildMs={9:F2}",
            ProfilerKey,
            reason,
            old?.Key.ToString() ?? "<none>",
            ActiveGeneration.Key,
            DescribeResourceGenerationKeyDelta(old?.Key, ActiveGeneration.Key),
            ActiveGeneration.TextureCount,
            ActiveGeneration.FrameBufferCount,
            ActiveGeneration.BufferCount,
            ActiveGeneration.RenderBufferCount,
            ActiveGeneration.BuildDuration.TotalMilliseconds);
    }

    private bool TryBuildCurrentViewportGenerationKey(out ResourceGenerationKey key)
    {
        XRViewport? viewport = RenderState.WindowViewport ?? LastWindowViewport;
        if (viewport is null)
        {
            key = default;
            return false;
        }

        key = BuildResourceGenerationKey(
            Math.Max(1, viewport.Width),
            Math.Max(1, viewport.Height),
            Math.Max(1, viewport.InternalWidth),
            Math.Max(1, viewport.InternalHeight));
        return true;
    }

    private void RetireGeneration(RenderResourceGeneration generation, string reason)
    {
        generation.MarkRetired(reason);
        _retiredGenerations.Enqueue(generation);
        Debug.Rendering(
            "[RenderResources] Generation retired. Pipeline={0} Reason={1} Key={2} Textures={3} FBOs={4} Buffers={5} RenderBuffers={6} RetiredQueue={7}",
            ProfilerKey,
            reason,
            generation.Key,
            generation.TextureCount,
            generation.FrameBufferCount,
            generation.BufferCount,
            generation.RenderBufferCount,
            _retiredGenerations.Count);

        while (_retiredGenerations.Count > MaxRetiredResourceGenerations)
        {
            WaitForGpuBeforePhysicalResourceDestruction("RetiredRenderResourceGenerationCap");
            RenderResourceGeneration retired = _retiredGenerations.Dequeue();
            Debug.Rendering(
                "[RenderResources] Disposing retired generation after queue cap. Pipeline={0} Reason=RetiredRenderResourceGenerationCap Key={1} RemainingQueue={2}",
                ProfilerKey,
                retired.Key,
                _retiredGenerations.Count);
            retired.Dispose();
        }
    }

    public void DestroyCache()
    {
        if (!RuntimeEngine.IsRenderThread)
        {
            EnqueueDestroyCache();
            return;
        }

        DestroyCacheOnRenderThread();
    }

    private void DestroyCacheIfResourcesExist()
    {
        if (!HasAnyTrackedResources())
            return;

        DestroyCache();
    }

    private void EnqueueDestroyCache()
    {
        if (System.Threading.Interlocked.Exchange(ref _destroyCacheQueued, 1) != 0)
            return;

        RuntimeEngine.EnqueueRenderThreadTask(() =>
        {
            try
            {
                DestroyCacheOnRenderThread();
            }
            finally
            {
                System.Threading.Volatile.Write(ref _destroyCacheQueued, 0);
            }
        }, "XRRenderPipelineInstance.DestroyCache", RenderThreadJobKind.RenderPipelineResource);
    }

    private void DestroyCacheOnRenderThread()
    {
        if (!HasAnyTrackedResources())
            return;

        LogDefaultRenderPipelineResourceDestruction("DestroyCache");
        WaitForGpuBeforePhysicalResourceDestruction("DestroyCache");
        PendingGeneration?.Dispose();
        PendingGeneration = null;
        ActiveGeneration?.Dispose();
        ActiveGeneration = null;
        while (_retiredGenerations.Count != 0)
            _retiredGenerations.Dequeue().Dispose();
        _legacyResources.DestroyAllPhysicalResources();
    }

    /// <summary>
    /// Destroys GPU resource instances but retains descriptor metadata so the
    /// command chain's cache-or-create commands can recreate them on the next frame
    /// without losing registry structure.
    /// </summary>
    public void InvalidatePhysicalResources()
    {
        if (EnqueueResourceMutationIfOffRenderThread(InvalidatePhysicalResources, "XRRenderPipelineInstance.InvalidatePhysicalResources"))
            return;

        if (!HasAnyTrackedResources())
            return;

        XRViewport? viewport = RenderState.WindowViewport ?? LastWindowViewport;
        if (viewport is not null
            && RequestResourceGeneration(
                Math.Max(1, viewport.Width),
                Math.Max(1, viewport.Height),
                Math.Max(1, viewport.InternalWidth),
                Math.Max(1, viewport.InternalHeight),
                "InvalidatePhysicalResources"))
        {
            return;
        }

        LogDefaultRenderPipelineResourceDestruction($"InvalidatePhysicalResources (generation {ResourceGeneration} -> {ResourceGeneration + 1})");
        WaitForGpuBeforePhysicalResourceDestruction("InvalidatePhysicalResources");
        Resources.DestroyAllPhysicalResources(retainDescriptors: true);
        ResourceGeneration++;
    }

    private bool HasAnyTrackedResources()
        => ActiveGeneration is not null
        || PendingGeneration is not null
        || _retiredGenerations.Count != 0
        || _legacyResources.TextureRecords.Count != 0
        || _legacyResources.FrameBufferRecords.Count != 0
        || _legacyResources.BufferRecords.Count != 0
        || _legacyResources.RenderBufferRecords.Count != 0;

    internal void RemoveTextureResource(string name, string reason)
    {
        if (EnqueueResourceMutationIfOffRenderThread(() => RemoveTextureResource(name, reason), $"XRRenderPipelineInstance.RemoveTextureResource[{name}]"))
            return;

        if (_pipeline is DefaultRenderPipeline pipeline
            && Resources.TryGetTexture(name, out XRTexture? texture)
            && texture is not null)
        {
            pipeline.LogTextureDestroy(this, name, texture, reason);
        }

        WaitForGpuBeforePhysicalResourceDestruction($"RemoveTextureResource[{name}]");
        Resources.RemoveTexture(name);
    }

    internal void RemoveFrameBufferResource(string name, string reason)
    {
        if (EnqueueResourceMutationIfOffRenderThread(() => RemoveFrameBufferResource(name, reason), $"XRRenderPipelineInstance.RemoveFrameBufferResource[{name}]"))
            return;

        if (_pipeline is DefaultRenderPipeline pipeline
            && Resources.TryGetFrameBuffer(name, out XRFrameBuffer? frameBuffer)
            && frameBuffer is not null)
        {
            pipeline.LogFrameBufferDestroy(this, name, frameBuffer, reason);
        }

        WaitForGpuBeforePhysicalResourceDestruction($"RemoveFrameBufferResource[{name}]");
        Resources.RemoveFrameBuffer(name);
    }

    private static void WaitForGpuBeforePhysicalResourceDestruction(string reason)
    {
        if (AbstractRenderer.Current is not VulkanRenderer renderer)
            return;

        renderer.DeviceWaitIdle();
        if (renderer.IsDeviceLost)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.RenderPipeline.ResourceDestroy.DeviceLost.{reason}",
                System.TimeSpan.FromSeconds(1),
                "[Vulkan] Skipping descriptor-reference release after DeviceWaitIdle reported device loss: {0}",
                reason);
            return;
        }

        renderer.ReleaseDescriptorReferencesForPhysicalResourceDestruction(reason);
        Debug.VulkanEvery(
            $"Vulkan.RenderPipeline.ResourceDestroy.WaitIdle.{reason}",
            System.TimeSpan.FromSeconds(1),
            "[Vulkan] DeviceWaitIdle before render-pipeline physical resource destruction: {0}",
            reason);
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
        XRViewport? viewport = RenderState.WindowViewport ?? LastWindowViewport;
        int displayWidth = Math.Max(1, viewport?.Width ?? internalWidth);
        int displayHeight = Math.Max(1, viewport?.Height ?? internalHeight);
        if (RequestResourceGeneration(
            displayWidth,
            displayHeight,
            Math.Max(1, internalWidth),
            Math.Max(1, internalHeight),
            "InternalResolutionResized"))
        {
            return;
        }

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
        if (!RuntimeEngine.IsRenderThread && existingTexture is not null && !ReferenceEquals(existingTexture, texture))
        {
            RuntimeEngine.EnqueueRenderThreadTask(
                () => SetTexture(texture, descriptor),
                $"XRRenderPipelineInstance.SetTexture[{name}]",
                RenderThreadJobKind.RenderPipelineResource);
            return;
        }

        if (!ReferenceEquals(existingTexture, texture) && _pipeline is DefaultRenderPipeline pipeline)
            pipeline.LogTextureBinding(this, name, texture, existingTexture);

        bool bindingChanged = !ReferenceEquals(existingTexture, texture);
        Resources.BindTexture(texture, descriptor);
        if (bindingChanged)
            NotifyRenderResourcesChanged();
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

        Resources.TryGetBuffer(name, out XRDataBuffer? existingBuffer);
        if (!RuntimeEngine.IsRenderThread && existingBuffer is not null && !ReferenceEquals(existingBuffer, buffer))
        {
            RuntimeEngine.EnqueueRenderThreadTask(
                () => SetBuffer(buffer, descriptor),
                $"XRRenderPipelineInstance.SetBuffer[{name}]",
                RenderThreadJobKind.RenderPipelineResource);
            return;
        }

        bool bindingChanged = !ReferenceEquals(existingBuffer, buffer);
        Resources.BindBuffer(buffer, descriptor);
        if (bindingChanged)
            NotifyRenderResourcesChanged();
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
        if (!RuntimeEngine.IsRenderThread && existingFbo is not null && !ReferenceEquals(existingFbo, fbo))
        {
            RuntimeEngine.EnqueueRenderThreadTask(
                () => SetFBO(fbo, descriptor),
                $"XRRenderPipelineInstance.SetFBO[{name}]",
                RenderThreadJobKind.Framebuffer);
            return;
        }

        if (!ReferenceEquals(existingFbo, fbo) && _pipeline is DefaultRenderPipeline pipeline)
            pipeline.LogFrameBufferBinding(this, name, fbo, existingFbo);

        bool bindingChanged = !ReferenceEquals(existingFbo, fbo);
        Resources.BindFrameBuffer(fbo, descriptor);
        if (bindingChanged)
            NotifyRenderResourcesChanged();
    }

    internal void NotifyRenderResourcesChanged()
        => AbstractRenderer.Current?.NotifyRenderResourcesChanged();

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

        Resources.TryGetRenderBuffer(name, out XRRenderBuffer? existingRenderBuffer);
        if (!RuntimeEngine.IsRenderThread && existingRenderBuffer is not null && !ReferenceEquals(existingRenderBuffer, renderBuffer))
        {
            RuntimeEngine.EnqueueRenderThreadTask(
                () => SetRenderBuffer(renderBuffer, descriptor),
                $"XRRenderPipelineInstance.SetRenderBuffer[{name}]",
                RenderThreadJobKind.RenderPipelineResource);
            return;
        }

        bool bindingChanged = !ReferenceEquals(existingRenderBuffer, renderBuffer);
        Resources.BindRenderBuffer(renderBuffer, descriptor);
        if (bindingChanged)
            NotifyRenderResourcesChanged();
    }

    private void ValidateActiveGenerationDescriptorParity()
    {
        RenderResourceGeneration? generation = ActiveGeneration;
        if (generation is null || _lastDescriptorParityGeneration == ResourceGeneration)
            return;

        _lastDescriptorParityGeneration = ResourceGeneration;
        RenderResourceRegistry registry = generation.Registry;

        foreach (RenderPipelineResourceSpec spec in generation.Layout.OrderedSpecs)
        {
            switch (spec)
            {
                case TextureSpec textureSpec:
                    ValidateTextureDescriptorParity(generation, registry, textureSpec);
                    break;
                case TextureViewSpec textureViewSpec:
                    ValidateTextureDescriptorParity(generation, registry, textureViewSpec);
                    break;
                case FrameBufferSpec frameBufferSpec:
                    ValidateFrameBufferDescriptorParity(generation, registry, frameBufferSpec);
                    break;
                case RenderBufferSpec renderBufferSpec:
                    ValidateRenderBufferDescriptorParity(generation, registry, renderBufferSpec);
                    break;
                case BufferSpec bufferSpec:
                    ValidateBufferDescriptorParity(generation, registry, bufferSpec);
                    break;
            }
        }
    }

    private void ValidateTextureDescriptorParity(
        RenderResourceGeneration generation,
        RenderResourceRegistry registry,
        TextureSpec spec)
    {
        if (!registry.TextureRecords.TryGetValue(spec.Name, out RenderTextureResource? record))
        {
            WarnMissingDeclaredResource(generation, spec);
            return;
        }

        TextureResourceDescriptor expected = spec.ToDescriptor();
        TextureResourceDescriptor actual = record.Descriptor;
        if (expected.Lifetime != actual.Lifetime
            || expected.SizePolicy != actual.SizePolicy
            || !string.Equals(expected.FormatLabel, actual.FormatLabel, StringComparison.Ordinal)
            || expected.StereoCompatible != actual.StereoCompatible
            || expected.ArrayLayers != actual.ArrayLayers
            || expected.SupportsAliasing != actual.SupportsAliasing
            || expected.RequiresStorageUsage != actual.RequiresStorageUsage)
        {
            WarnDescriptorMismatch(generation, spec.Name, "texture", DescribeTextureDescriptor(expected), DescribeTextureDescriptor(actual));
        }
    }

    private void ValidateTextureDescriptorParity(
        RenderResourceGeneration generation,
        RenderResourceRegistry registry,
        TextureViewSpec spec)
    {
        if (!registry.TextureRecords.TryGetValue(spec.Name, out RenderTextureResource? record))
        {
            WarnMissingDeclaredResource(generation, spec);
            return;
        }

        TextureResourceDescriptor expected = spec.ToDescriptor();
        TextureResourceDescriptor actual = record.Descriptor;
        if (expected.Lifetime != actual.Lifetime
            || expected.SizePolicy != actual.SizePolicy
            || !string.Equals(expected.FormatLabel, actual.FormatLabel, StringComparison.Ordinal)
            || expected.StereoCompatible != actual.StereoCompatible
            || expected.ArrayLayers != actual.ArrayLayers)
        {
            WarnDescriptorMismatch(generation, spec.Name, "texture view", DescribeTextureDescriptor(expected), DescribeTextureDescriptor(actual));
        }
    }

    private void ValidateFrameBufferDescriptorParity(
        RenderResourceGeneration generation,
        RenderResourceRegistry registry,
        FrameBufferSpec spec)
    {
        if (!registry.FrameBufferRecords.TryGetValue(spec.Name, out RenderFrameBufferResource? record))
        {
            WarnMissingDeclaredResource(generation, spec);
            return;
        }

        FrameBufferResourceDescriptor expected = spec.ToDescriptor();
        FrameBufferResourceDescriptor actual = record.Descriptor;
        if (expected.Lifetime != actual.Lifetime
            || expected.SizePolicy != actual.SizePolicy
            || !AttachmentDescriptorsEqual(expected.Attachments, actual.Attachments))
        {
            WarnDescriptorMismatch(generation, spec.Name, "framebuffer", DescribeFrameBufferDescriptor(expected), DescribeFrameBufferDescriptor(actual));
        }
    }

    private void ValidateRenderBufferDescriptorParity(
        RenderResourceGeneration generation,
        RenderResourceRegistry registry,
        RenderBufferSpec spec)
    {
        if (!registry.RenderBufferRecords.TryGetValue(spec.Name, out RenderRenderBufferResource? record))
        {
            WarnMissingDeclaredResource(generation, spec);
            return;
        }

        RenderBufferResourceDescriptor expected = spec.ToDescriptor();
        RenderBufferResourceDescriptor actual = record.Descriptor;
        if (expected.Lifetime != actual.Lifetime
            || expected.SizePolicy != actual.SizePolicy
            || expected.StorageFormat != actual.StorageFormat
            || expected.MultisampleCount != actual.MultisampleCount
            || expected.DefaultAttachment != actual.DefaultAttachment)
        {
            WarnDescriptorMismatch(generation, spec.Name, "renderbuffer", expected.ToString(), actual.ToString());
        }
    }

    private void ValidateBufferDescriptorParity(
        RenderResourceGeneration generation,
        RenderResourceRegistry registry,
        BufferSpec spec)
    {
        if (!registry.BufferRecords.TryGetValue(spec.Name, out RenderBufferResource? record))
        {
            WarnMissingDeclaredResource(generation, spec);
            return;
        }

        BufferResourceDescriptor expected = spec.ToDescriptor();
        BufferResourceDescriptor actual = record.Descriptor;
        if (expected.Lifetime != actual.Lifetime
            || expected.SizeInBytes != actual.SizeInBytes
            || expected.Target != actual.Target
            || expected.Usage != actual.Usage
            || expected.SupportsAliasing != actual.SupportsAliasing
            || expected.ElementStride != actual.ElementStride
            || expected.ElementCount != actual.ElementCount
            || expected.AccessPattern != actual.AccessPattern)
        {
            WarnDescriptorMismatch(generation, spec.Name, "buffer", expected.ToString(), actual.ToString());
        }
    }

    private void WarnMissingDeclaredResource(RenderResourceGeneration generation, RenderPipelineResourceSpec spec)
    {
        if (!spec.Required)
            return;

        Debug.RenderingWarning(
            "[RenderResources] Declared resource missing after frame execution. Pipeline={0} Generation={1} Resource={2} Kind={3}",
            ProfilerKey,
            generation.Key,
            spec.Name,
            spec.Kind);
    }

    private void WarnDescriptorMismatch(
        RenderResourceGeneration generation,
        string name,
        string kind,
        string? expected,
        string? actual)
        => Debug.RenderingWarning(
            "[RenderResources] Descriptor parity mismatch. Pipeline={0} Generation={1} Resource={2} Kind={3} Expected={4} Actual={5}",
            ProfilerKey,
            generation.Key,
            name,
            kind,
            expected ?? "<null>",
            actual ?? "<null>");

    private static string DescribeTextureDescriptor(TextureResourceDescriptor descriptor)
        => $"lifetime={descriptor.Lifetime},size={descriptor.SizePolicy},format={descriptor.FormatLabel},stereo={descriptor.StereoCompatible},layers={descriptor.ArrayLayers},alias={descriptor.SupportsAliasing},storage={descriptor.RequiresStorageUsage}";

    private static string DescribeFrameBufferDescriptor(FrameBufferResourceDescriptor descriptor)
        => $"lifetime={descriptor.Lifetime},size={descriptor.SizePolicy},attachments={DescribeAttachments(descriptor.Attachments)}";

    private static string DescribeAttachments(IReadOnlyList<FrameBufferAttachmentDescriptor> attachments)
    {
        if (attachments.Count == 0)
            return "[]";

        string[] parts = new string[attachments.Count];
        for (int i = 0; i < attachments.Count; i++)
        {
            FrameBufferAttachmentDescriptor attachment = attachments[i];
            parts[i] = $"{attachment.Attachment}:{attachment.ResourceName}:mip{attachment.MipLevel}:layer{attachment.LayerIndex}";
        }
        return "[" + string.Join(",", parts) + "]";
    }

    private static bool AttachmentDescriptorsEqual(
        IReadOnlyList<FrameBufferAttachmentDescriptor> left,
        IReadOnlyList<FrameBufferAttachmentDescriptor> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
            if (left[i] != right[i])
                return false;

        return true;
    }

    private void WarnIfScreenSpaceUiHasNoRenderCommand(IRuntimeScreenSpaceUserInterface? userInterface, XRViewport? viewport)
    {
        if (Pipeline?.CommandChain is null || userInterface is null)
            return;

        if (!userInterface.IsScreenSpace)
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
