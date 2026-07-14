using ImageMagick;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Occlusion;
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
    private const double FailedGenerationRetryBackoffMilliseconds = 1000.0;
    private const double IncrementalGenerationSliceMilliseconds = 2.0;
    private const int IncrementalGenerationMaxSpecsPerSlice = 4;
    private readonly RenderPipelineResourceManager _resourceManager = new();
    private readonly Queue<RenderResourceGeneration> _retiredGenerations = new();
    private readonly RenderResourceRegistry _legacyResources = new();
    private ResourceBuildContext? _resourceBuildContext;
    private bool _requiresManagedResourceGeneration;

    /// <summary>
    /// Stable, monotonically increasing identifier assigned at construction. Used by
    /// per-instance diagnostics (GPU profiler hierarchies, logging) so multiple
    /// instances of the same pipeline type are not aggregated into a single bucket.
    /// </summary>
    public int InstanceId { get; } = System.Threading.Interlocked.Increment(ref s_nextInstanceId);

    private OcclusionViewOwnership _occlusionViewOwnership;

    public OcclusionViewOwnership OcclusionViewOwnership
        => _occlusionViewOwnership.HasScopeOverride
            ? _occlusionViewOwnership
            : _occlusionViewOwnership.WithResourceGeneration(ResourceGeneration);

    public XRRenderPipelineInstance()
    {
        _occlusionViewOwnership = OcclusionViewOwnership.Independent(InstanceId);
        MeshRenderCommands.SetOwnerPipeline(this);
    }

    /// <summary>
    /// Assigns this physical pipeline to an output POV family. OpenXR uses this
    /// to aggregate independently recorded eye queries without sharing command
    /// collections or query objects.
    /// </summary>
    public void ConfigureOcclusionViewOwnership(OcclusionViewOwnership ownership)
    {
        if (!ownership.IsValid)
            throw new ArgumentException("Occlusion ownership must identify a physical pipeline and non-empty POV coverage.", nameof(ownership));
        if (ownership.PipelineInstanceId != InstanceId)
            throw new ArgumentException($"Occlusion ownership pipeline {ownership.PipelineInstanceId} does not match instance {InstanceId}.", nameof(ownership));
        if (_occlusionViewOwnership.Equals(ownership))
            return;

        _occlusionViewOwnership = ownership;
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

    /// <summary>
    /// Command collection currently bound to the render state. Viewports such as OpenXR stereo eyes can
    /// provide a shared visibility collection while keeping separate pipeline instances/resources.
    /// </summary>
    public RenderCommandCollection ActiveMeshRenderCommands => RenderState.MeshRenderCommands ?? MeshRenderCommands;

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
    private ResourceGenerationKey? _lastFailedGenerationKey;
    private long _failedGenerationRetryAfterTimestamp;
    private ulong _resizeCatchUpSkippedFrameId = ulong.MaxValue;

    /// <summary>
    /// Monotonically increasing counter incremented each time physical GPU resources
    /// are invalidated (e.g., after a viewport resize). Command containers compare
    /// their last-allocated generation against this value to detect stale state and
    /// force re-allocation of per-command resources such as fullscreen quads.
    /// </summary>
    public int ResourceGeneration { get; private set; }
    internal IRenderResourceGenerationBackend? ResourceGenerationBackendOverride { get; set; }

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

    /// <summary>
    /// Called when a property is about to change. 
    /// This method can be overridden to perform custom logic before the property value is updated.
    /// </summary>
    /// <typeparam name="T">The type of the property.</typeparam>
    /// <param name="propName">The name of the property that is changing.</param>
    /// <param name="field">The current value of the property.</param>
    /// <param name="new">The new value of the property.</param>
    /// <returns>True if the property value should be changed; otherwise, false.</returns>
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

    /// <summary>
    /// Called when a property has changed. 
    /// This method can be overridden to perform custom logic after the property value has been updated.
    /// </summary>
    /// <typeparam name="T">The type of the property.</typeparam>
    /// <param name="propName">The name of the property that has changed.</param>
    /// <param name="prev">The previous value of the property.</param>
    /// <param name="field">The current value of the property.</param>
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
    
    /// <summary>
    /// Collects the visible state of the scene for rendering. 
    /// This state is used to determine which objects are visible and should be rendered in the current frame.
    /// </summary>
    public RenderingState CollectVisibleState { get; } = new();
    /// <summary>
    /// Holds the current rendering state, including information about the scene, camera, viewport, and other rendering parameters.
    /// </summary>
    public RenderingState RenderState { get; } = new();
    /// <summary>
    /// Gets or sets the material to use when an invalid material is encountered during rendering.
    /// </summary>
    public XRMaterial? InvalidMaterial { get; set; }

    /// <summary>
    /// Renders the scene to the viewport or framebuffer.
    /// </summary>
    /// <param name="scene">The scene to be rendered.</param>
    /// <param name="camera">The camera through which the scene is viewed.</param>
    /// <param name="stereoRightEyeCamera">The camera for the right eye in stereo rendering.</param>
    /// <param name="viewport">The viewport defining the rendering area.</param>
    /// <param name="targetFBO">The target framebuffer object for rendering.</param>
    /// <param name="userInterface">The user interface to be rendered on top of the scene.</param>
    /// <param name="shadowPass">Indicates whether this is a shadow pass.</param>
    /// <param name="stereoPass">Indicates whether this is a stereo rendering pass.</param>
    /// <param name="shadowMaterial">The material to use for shadow rendering.</param>
    /// <param name="meshRenderCommandsOverride">An optional override for the mesh render commands to be used during rendering.</param>
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
        EAntiAliasingMode effectiveAntiAliasingMode =
            effectiveAntiAliasingCamera?.AntiAliasingModeOverride
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
            if (viewport.AllowAutomaticInternalResolution)
            {
                float? requestedScale = Pipeline.GetRequestedInternalResolutionForCamera(
                    effectiveAntiAliasingCamera,
                    effectiveAntiAliasingMode);

                // Avoid redundant resets, while still reapplying the scale after a display resize.
                if (requestedScale.HasValue)
                {
                    float scale = Math.Clamp(requestedScale.Value, 0.25f, 1.25f);
                    int expectedWidth = Math.Max(1, (int)(scale * viewport.Width));
                    int expectedHeight = Math.Max(1, (int)(scale * viewport.Height));
                    if (_appliedInternalResolutionScale != scale ||
                        viewport.InternalWidth != expectedWidth ||
                        viewport.InternalHeight != expectedHeight)
                    {
                        _appliedInternalResolutionScale = scale;
                        viewport.SetInternalResolution(expectedWidth, expectedHeight, correctAspect: false);
                    }
                }
                else if (_appliedInternalResolutionScale.HasValue)
                {
                    // Restore to native internal resolution once the request is cleared.
                    _appliedInternalResolutionScale = null;
                    viewport.SetInternalResolution(viewport.Width, viewport.Height, true);
                }
            }
            else
            {
                _appliedInternalResolutionScale = null;
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

                using (AbstractRenderer.Current?.EnterRenderPipelineFrameResourceScope(this, viewport))
                {
                    Pipeline.CommandChain.Execute();
                }

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

    /// <summary>
    /// Enqueues a resource mutation action to be executed on the render thread if the current thread is not the render thread.
    /// </summary>
    /// <param name="action">The action to execute on the render thread.</param>
    /// <param name="reason">The reason for enqueuing the action.</param>
    /// <param name="renderThreadKind">The kind of render thread job.</param>
    /// <returns>True if the action was enqueued; false if it was executed immediately on the render thread.</returns>
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

    /// <summary>
    /// Ensures that the necessary resources for the current frame are generated and ready for use. If resources are not ready, it may request resource generation or prepare pending generations as needed.
    /// </summary>
    /// <param name="viewport">The viewport for which to ensure resource generation.</param>
    /// <returns>True if resource generation is ensured; false otherwise.</returns>
    private bool EnsureResourceGenerationForCurrentFrame(XRViewport? viewport)
    {
        DrainRetiredGenerations();

        if (Pipeline is null || viewport is null)
            return true;

        if (ShouldDeferResourceGenerationForInteractiveWindowResize(viewport) && ActiveGeneration is not null)
        {
            DiscardPendingGeneration("InteractiveResize");
            return true;
        }

        var dimensions = ResolveViewportResourceDimensions(viewport);
        ResourceGenerationKey key = BuildResourceGenerationKey(
            dimensions.DisplayWidth,
            dimensions.DisplayHeight,
            dimensions.InternalWidth,
            dimensions.InternalHeight,
            viewport);

        if (viewport.RendersToExternalSwapchainTarget)
            return EnsureExternalSwapchainResourceGenerationForCurrentFrame(viewport, key);

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
            // because they have no active managed generation. Pipelines that do
            // declare a managed layout must fail closed here; otherwise a stale
            // or discarded initial generation can render one frame against the
            // partial legacy registry while still carrying full pass metadata.
            return PendingGeneration is null && !_requiresManagedResourceGeneration;
        }

        if (ActiveGeneration.Key == key)
            return true;

        return !IsResizeOnlyGenerationDelta(ActiveGeneration.Key, key);
    }

    /// <summary>
    /// Ensures that the necessary resources for the current frame are generated and ready for use when rendering to an external swapchain target. If resources are not ready, it may request resource generation or prepare pending generations as needed. This method is specifically designed to handle the requirements of external swapchain rendering, ensuring that the active generation matches the required resource generation key.
    /// </summary>
    /// <param name="viewport">The viewport for which to ensure resource generation.</param>
    /// <param name="key">The resource generation key that must be satisfied.</param>
    /// <returns>True if resource generation is ensured; false otherwise.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    private bool EnsureExternalSwapchainResourceGenerationForCurrentFrame(
        XRViewport viewport,
        ResourceGenerationKey key)
    {
        if (ActiveGeneration is null && PendingGeneration is null)
            RequestResourceGeneration(key, "ExternalSwapchainInitial", force: true);
        else if (ActiveGeneration is null || ActiveGeneration.Key != key)
            RequestResourceGeneration(key, "ExternalSwapchainFrameProfileChanged", force: true);

        TryPreparePendingGeneration(
            "ExternalSwapchainFramePrepare",
            forceDue: true,
            catchUpMaxDuration: TimeSpan.MaxValue,
            catchUpMaxSpecsPerSlice: int.MaxValue);

        if (ActiveGeneration?.Key == key)
            return true;

        if (PendingGeneration is not null)
        {
            Debug.RenderingEvery(
                $"RenderResources.ExternalSwapchainPending.{ProfilerKey}",
                TimeSpan.FromMilliseconds(250),
                "[RenderResources] Skipping external swapchain command chain until resources are ready. Pipeline={0} Active={1} Pending={2} Viewport={3}x{4}/{5}x{6}",
                ProfilerKey,
                ActiveGeneration?.Key.ToString() ?? "<none>",
                PendingGeneration,
                viewport.Width,
                viewport.Height,
                viewport.InternalWidth,
                viewport.InternalHeight);
            return false;
        }

        string active = ActiveGeneration?.Key.ToString() ?? "<none>";
        string pending = PendingGeneration?.Key.ToString() ?? "<none>";
        throw new InvalidOperationException(
            "OpenXR external swapchain resources did not match the required eye extent. " +
            $"Required={key}; Active={active}; Pending={pending}; " +
            $"Viewport={viewport.Width}x{viewport.Height} internal={viewport.InternalWidth}x{viewport.InternalHeight}.");
    }

    /// <summary>
    /// Determines whether the difference between two resource generation keys is solely due to a change in viewport dimensions (resize) while all other properties remain the same. This is used to identify cases where only the size of the resources has changed, allowing for optimized handling of resource regeneration.
    /// </summary>
    /// <param name="oldKey">The original resource generation key.</param>
    /// <param name="newKey">The new resource generation key to compare against.</param>
    /// <returns>True if the only difference between the keys is a change in viewport dimensions; false otherwise.</returns>
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

    /// <summary>
    /// Resolves the display and internal dimensions for a given viewport, ensuring that they are valid and non-zero. This method takes into account the viewport's properties and any external swapchain targets to determine the appropriate dimensions for resource generation.
    /// </summary>
    /// <param name="viewport">The viewport for which to resolve resource dimensions.</param>
    /// <returns>A tuple containing the resolved display and internal widths and heights.</returns>
    internal static (int DisplayWidth, int DisplayHeight, int InternalWidth, int InternalHeight) ResolveViewportResourceDimensions(XRViewport viewport)
        => ResolveViewportResizeResourceDimensions(
            viewport,
            viewport.Width,
            viewport.Height,
            viewport.InternalWidth,
            viewport.InternalHeight);

    /// <summary>
    /// Resolves the display and internal dimensions for a given viewport, ensuring that they are valid and non-zero. This method takes into account the viewport's properties and any external swapchain targets to determine the appropriate dimensions for resource generation. It guarantees that the returned dimensions are at least 1x1, preventing invalid resource sizes.
    /// </summary>
    /// <param name="viewport">The viewport for which to resolve resource dimensions.</param>
    /// <param name="displayWidth">The requested display width of the viewport.</param>
    /// <param name="displayHeight">The requested display height of the viewport.</param>
    /// <param name="internalWidth">The requested internal width of the viewport.</param>
    /// <param name="internalHeight">The requested internal height of the viewport.</param>
    /// <returns>A tuple containing the resolved display and internal widths and heights.</returns>
    internal static (int DisplayWidth, int DisplayHeight, int InternalWidth, int InternalHeight) ResolveViewportResizeResourceDimensions(
        XRViewport? viewport,
        int displayWidth,
        int displayHeight,
        int internalWidth,
        int internalHeight)
    {
        int resolvedDisplayWidth = Math.Max(1, displayWidth);
        int resolvedDisplayHeight = Math.Max(1, displayHeight);
        int resolvedInternalWidth = Math.Max(1, internalWidth > 0 ? internalWidth : resolvedDisplayWidth);
        int resolvedInternalHeight = Math.Max(1, internalHeight > 0 ? internalHeight : resolvedDisplayHeight);

        return (
            resolvedDisplayWidth,
            resolvedDisplayHeight,
            resolvedInternalWidth,
            resolvedInternalHeight);
    }

    /// <summary>
    /// Determines whether resource generation should be deferred during an interactive window resize operation. This is used to avoid unnecessary resource regeneration while the user is actively resizing the window, which can lead to performance issues or visual artifacts. The method checks if the viewport does not render to an external swapchain target and if the associated window is currently undergoing an interactive resize.
    /// </summary>
    /// <param name="viewport">The viewport to check for interactive window resize.</param>
    /// <returns>True if resource generation should be deferred; otherwise, false.</returns>
    private static bool ShouldDeferResourceGenerationForInteractiveWindowResize(XRViewport viewport)
        => !viewport.RendersToExternalSwapchainTarget &&
           viewport.Window?.IsInteractiveResizeInProgress == true;

    /// <summary>
    /// Requests the generation of rendering resources based on the specified display and internal dimensions, along with an optional viewport. This method constructs a resource generation key from the provided parameters and delegates to the internal resource generation request method. It allows for forcing the generation even if the requested key matches the active or pending generation.
    /// </summary>
    /// <param name="displayWidth">The requested display width of the viewport.</param>
    /// <param name="displayHeight">The requested display height of the viewport.</param>
    /// <param name="internalWidth">The requested internal width of the viewport.</param>
    /// <param name="internalHeight">The requested internal height of the viewport.</param>
    /// <param name="reason">The reason for requesting resource generation.</param>
    /// <param name="force">Whether to force resource generation even if the key matches the active or pending generation.</param>
    /// <param name="viewport">The viewport for which to generate resources.</param>
    /// <returns>True if the resource generation request was successfully enqueued or processed; otherwise, false.</returns>
    internal bool RequestResourceGeneration(
        int displayWidth,
        int displayHeight,
        int internalWidth,
        int internalHeight,
        string reason,
        bool force = false,
        XRViewport? viewport = null)
        => RequestResourceGeneration(
            BuildResourceGenerationKey(displayWidth, displayHeight, internalWidth, internalHeight, viewport),
            reason,
            force);

    /// <summary>
    /// Requests the generation of rendering resources based on the specified resource generation key. If the requested key matches the active or pending generation, it may skip redundant generation. If a backoff is active due to previous failed generations, it will delay the request. Otherwise, it will build a new resource layout and enqueue a pending generation if necessary.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="reason"></param>
    /// <param name="force"></param>
    /// <returns>True if the resource generation request was successfully enqueued or processed; otherwise, false.</returns>
    private bool RequestResourceGeneration(ResourceGenerationKey key, string reason, bool force = false)
    {
        RenderPipeline? pipeline = _pipeline;
        if (pipeline is null)
            return false;

        if (!force && ActiveGeneration?.Key == key)
        {
            DiscardPendingGeneration($"Active generation already matches request: {reason}");
            return true;
        }

        if (!force && PendingGeneration?.Key == key)
            return true;

        if (IsGenerationRetryBackoffActive(key, out double retryBackoffRemainingMilliseconds))
        {
            Debug.RenderingEvery(
                $"RenderResources.FailedGenerationBackoff.{ProfilerKey}",
                TimeSpan.FromMilliseconds(250),
                "[RenderResources] Generation request delayed after failed materialization. Pipeline={0} Reason={1} Target={2} RemainingMs={3:F0}",
                ProfilerKey,
                reason,
                key,
                retryBackoffRemainingMilliseconds);
            return false;
        }

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
        {
            _requiresManagedResourceGeneration = false;
            return false;
        }

        _requiresManagedResourceGeneration = true;

        if (ActiveGeneration?.Key == key && ActiveGeneration.Layout.IsStructurallyEquivalentTo(layout))
        {
            DiscardPendingGeneration($"Active generation layout already matches request: {reason}");
            Debug.Rendering(
                "[RenderResources] Generation request skipped because active layout already matches. Pipeline={0} Reason={1} Active={2} Resources={3}",
                ProfilerKey,
                reason,
                ActiveGeneration.Key,
                layout.OrderedSpecs.Count);
            return true;
        }
        else if (ActiveGeneration?.Key == key)
        {
            Debug.RenderingWarning(
                "[RenderResources] Same-key generation request has different layout. Pipeline={0} Reason={1} Key={2} Difference={3}",
                ProfilerKey,
                reason,
                key,
                ActiveGeneration.Layout.DescribeStructuralDifferenceTo(layout));
        }

        if (PendingGeneration?.Key == key && PendingGeneration.Layout.IsStructurallyEquivalentTo(layout))
            return true;

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

    /// <summary>
    /// Configures the debounce timing for a pending resource generation request. 
    /// This method determines whether the pending generation should be delayed 
    /// based on the current active generation and the reason for the request. 
    /// If debouncing is necessary, it calculates the appropriate timestamps 
    /// to ensure that resource generation is not triggered too frequently, 
    /// allowing for coalescing of multiple requests within a specified time window.
    /// </summary>
    /// <param name="key">The resource generation key for the pending generation.</param>
    /// <param name="reason">The reason for requesting the pending generation.</param>
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

    /// <summary>
    /// Determines whether the pending resource generation request should be debounced based on the current active generation and the reason for the request.
    /// </summary>
    /// <param name="key">The resource generation key for the pending generation.</param>
    /// <param name="reason">The reason for requesting the pending generation.</param>
    /// <returns>True if the pending generation should be debounced; otherwise, false.</returns>
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

    /// <summary>
    /// Describes the differences between two resource generation keys, highlighting the specific properties that have changed. 
    /// This method is useful for logging and debugging purposes, allowing developers to understand 
    /// what aspects of the resource generation have been modified between the old and new keys.
    /// </summary>
    /// <param name="oldKey">The previous resource generation key, or null if there was no previous key.</param>
    /// <param name="newKey">The new resource generation key to compare against the old key.</param>
    /// <returns>A string describing the differences between the old and new keys, or "initial" if there was no old key.</returns>
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

    /// <summary>
    /// Calculates the remaining time in milliseconds before a pending resource generation request is considered ready to be processed. This method checks the current timestamp against the configured debounce timestamp for the pending generation, returning the remaining time until the generation can proceed. If there is no pending generation or if the debounce period has already elapsed, it returns 0.0 milliseconds.
    /// </summary>
    /// <returns>The remaining time in milliseconds before the pending generation can proceed, or 0.0 if there is no pending generation or if the debounce period has elapsed.</returns>
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

    /// <summary>
    /// Converts a duration in milliseconds to the equivalent number of ticks used by the System.Diagnostics.Stopwatch class. This is useful for timing and scheduling operations based on high-resolution timestamps, allowing for precise control over time-based events in the rendering pipeline.
    /// </summary>
    /// <param name="milliseconds">The duration in milliseconds to convert to stopwatch ticks.</param>
    /// <returns>The equivalent number of stopwatch ticks for the given duration in milliseconds.</returns>
    private static long StopwatchTicksFromMilliseconds(double milliseconds)
        => (long)(milliseconds * System.Diagnostics.Stopwatch.Frequency / 1000.0);

    /// <summary>
    /// Builds a resource generation key based on the specified display and internal dimensions, along with an optional viewport. This key encapsulates the necessary parameters for generating rendering resources, including display size, internal resolution, HDR output, anti-aliasing mode, MSAA sample count, stereo rendering, and any additional feature flags. The generated key is used to determine whether existing resources can be reused or if new resources need to be generated for the current frame.
    /// </summary>
    /// <param name="displayWidth">The width of the display in pixels.</param>
    /// <param name="displayHeight">The height of the display in pixels.</param>
    /// <param name="internalWidth">The internal width used for rendering, which may differ from the display width for performance or quality reasons.</param>
    /// <param name="internalHeight">The internal height used for rendering, which may differ from the display height for performance or quality reasons.</param>
    /// <param name="viewport">An optional viewport defining the portion of the render target to use.</param>
    /// <returns>A ResourceGenerationKey encapsulating the specified rendering parameters.</returns>
    private ResourceGenerationKey BuildResourceGenerationKey(
        int displayWidth,
        int displayHeight,
        int internalWidth,
        int internalHeight,
        XRViewport? viewport = null)
    {
        RenderPipeline? pipeline = _pipeline;

        bool stereo = pipeline?.UsesStereoResources(this, viewport) ?? RenderState.StereoPass;
        XRCamera? viewportCamera = viewport?.ActiveCamera;

        bool outputHdr = EffectiveOutputHDRThisFrame
            ?? LastSceneCamera?.OutputHDROverride
            ?? LastRenderingCamera?.OutputHDROverride
            ?? viewportCamera?.OutputHDROverride
            ?? RuntimeRenderingHostServices.Current.DefaultOutputHDR;

        EAntiAliasingMode antiAliasingMode = EffectiveAntiAliasingModeThisFrame
            ?? LastSceneCamera?.AntiAliasingModeOverride
            ?? LastRenderingCamera?.AntiAliasingModeOverride
            ?? viewportCamera?.AntiAliasingModeOverride
            ?? RuntimeRenderingHostServices.Current.DefaultAntiAliasingMode;

        uint msaaSamples = Math.Max(1u,
            EffectiveMsaaSampleCountThisFrame
                ?? LastSceneCamera?.MsaaSampleCountOverride
                ?? LastRenderingCamera?.MsaaSampleCountOverride
                ?? viewportCamera?.MsaaSampleCountOverride
                ?? RuntimeRenderingHostServices.Current.DefaultMsaaSampleCount);

        ulong featureMask = pipeline?.BuildResourceFeatureMaskForGenerationKey(
            this,
            viewport ?? RenderState.WindowViewport ?? LastWindowViewport) ?? 0UL;

        uint reservedViewCount = stereo ? 2u : 1u;
        uint reservedEyeIndex = 0u;
        RenderPipelineExternalTargetKind externalTargetKind = viewport?.RendersToExternalSwapchainTarget == true
            ? RenderPipelineExternalTargetKind.ExternalSwapchain
            : RenderState.OutputFBO is not null
                ? RenderPipelineExternalTargetKind.CallerProvidedFrameBuffer
                : RenderPipelineExternalTargetKind.Window;

        return new ResourceGenerationKey(
            pipeline?.DebugName ?? DebugName,
            (uint)Math.Max(1, displayWidth),
            (uint)Math.Max(1, displayHeight),
            (uint)Math.Max(1, internalWidth),
            (uint)Math.Max(1, internalHeight),
            outputHdr,
            antiAliasingMode,
            msaaSamples,
            stereo,
            featureMask,
            reservedViewCount,
            reservedEyeIndex,
            externalTargetKind);
    }

    /// <summary>
    /// Attempts to prepare the pending resource generation for execution, checking if it is due based on the configured debounce timing. If the pending generation is ready and valid, it will be committed; otherwise, it may be deferred or discarded based on its state and the current active generation. This method provides a simplified interface for preparing pending generations without specifying additional parameters for force execution or catch-up behavior.
    /// </summary>
    /// <param name="reason">The reason for attempting to prepare the pending generation, used for logging and debugging purposes.</param>
    /// <returns>True if the pending generation was successfully prepared and committed; otherwise, false.</returns>
    private bool TryPreparePendingGeneration(string reason)
        => TryPreparePendingGeneration(
            reason,
            forceDue: false,
            catchUpMaxDuration: TimeSpan.Zero,
            catchUpMaxSpecsPerSlice: 0);

    /// <summary>
    /// Attempts to prepare the pending resource generation for execution, checking if it is due based on the configured debounce timing. If the pending generation is ready and valid, it will be committed; otherwise, it may be deferred or discarded based on its state and the current active generation. This method allows for specifying whether to force execution of the pending generation, as well as parameters for catch-up behavior in case of incremental materialization.
    /// </summary>
    /// <param name="reason">The reason for attempting to prepare the pending generation, used for logging and debugging purposes.</param>
    /// <param name="forceDue">Whether to force execution of the pending generation regardless of debounce timing.</param>
    /// <param name="catchUpMaxDuration">The maximum duration allowed for catch-up execution of incremental materialization.</param>
    /// <param name="catchUpMaxSpecsPerSlice">The maximum number of specifications to process per slice during catch-up execution.</param>
    /// <returns>True if the pending generation was successfully prepared and committed; otherwise, false.</returns>
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
            return CommitPendingGeneration(reason);

        if (TryBuildCurrentViewportGenerationKey(out ResourceGenerationKey currentKey) &&
            pending.Key != currentKey)
        {
            Debug.RenderingWarning(
                "[RenderResources] Pending generation is stale before materialization. Pipeline={0} Reason={1} Pending={2} Current={3} Delta={4}",
                ProfilerKey,
                reason,
                pending.Key,
                currentKey,
                DescribeResourceGenerationKeyDelta(pending.Key, currentKey));
            DiscardPendingGeneration($"StaleBeforeMaterialize:{reason}");
            return false;
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
            RegisterPendingGenerationFailure(pending.Key, reason);
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

        return CommitPendingGeneration(reason);
    }

    /// <summary>
    /// Determines whether the pending resource generation is due for execution based on the configured debounce timing. 
    /// This method checks the current timestamp against the timestamp indicating when the pending generation is ready to be processed. 
    /// If the pending generation is due, it returns true; otherwise, it returns false, indicating that the generation should be deferred until the appropriate time.
    /// </summary>
    /// <returns>True if the pending generation is due for execution; otherwise, false.</returns>
    private bool IsPendingGenerationDue()
    {
        long due = _pendingGenerationReadyAfterTimestamp;
        return due == 0 || System.Diagnostics.Stopwatch.GetTimestamp() >= due;
    }

    /// <summary>
    /// Clears the debounce timestamps for the pending resource generation, effectively resetting the timing for when the pending generation can be processed. 
    /// This method is called when a pending generation is either committed or discarded, ensuring that any previous debounce state does not affect future resource generation requests.
    /// </summary>
    private void ClearPendingGenerationDebounce()
    {
        _pendingGenerationReadyAfterTimestamp = 0;
        _pendingGenerationFirstResizeRequestTimestamp = 0;
    }

    /// <summary>
    /// Determines whether a backoff is currently active for retrying resource generation after a previous failure. If a backoff is active, it calculates the remaining time in milliseconds before the next retry attempt can be made. This method is used to prevent rapid successive attempts to generate resources after a failure, allowing for a controlled retry mechanism.
    /// </summary>
    /// <param name="key">The key identifying the resource generation request.</param>
    /// <param name="remainingMilliseconds">The remaining time in milliseconds before the next retry attempt can be made if a backoff is active.</param>
    /// <returns>True if a backoff is currently active; otherwise, false.</returns>
    private bool IsGenerationRetryBackoffActive(ResourceGenerationKey key, out double remainingMilliseconds)
    {
        remainingMilliseconds = 0.0;
        if (!_lastFailedGenerationKey.HasValue || _lastFailedGenerationKey.Value != key)
            return false;

        long retryAfter = _failedGenerationRetryAfterTimestamp;
        if (retryAfter == 0)
            return false;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now >= retryAfter)
        {
            ClearFailedGenerationBackoff(key);
            return false;
        }

        remainingMilliseconds = (retryAfter - now) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return true;
    }

    /// <summary>
    /// Registers a failure in the pending resource generation process, setting up a backoff period before the next retry attempt can be made. This method records the key of the failed generation and calculates the timestamp after which a retry can be attempted, based on a predefined backoff duration. It also logs a warning message indicating that a retry backoff has been armed, providing details about the pipeline, reason for failure, target key, and the duration of the backoff.
    /// </summary>
    /// <param name="key">The key identifying the resource generation request that failed.</param>
    /// <param name="reason">The reason for the failure.</param>
    private void RegisterPendingGenerationFailure(ResourceGenerationKey key, string reason)
    {
        _lastFailedGenerationKey = key;
        _failedGenerationRetryAfterTimestamp = System.Diagnostics.Stopwatch.GetTimestamp()
            + StopwatchTicksFromMilliseconds(FailedGenerationRetryBackoffMilliseconds);

        Debug.RenderingWarning(
            "[RenderResources] Generation retry backoff armed. Pipeline={0} Reason={1} Target={2} RetryAfterMs={3:F0}",
            ProfilerKey,
            reason,
            key,
            FailedGenerationRetryBackoffMilliseconds);
    }

    /// <summary>
    /// Clears the backoff state for a previously failed resource generation request, allowing for immediate retry attempts. This method resets the last failed generation key and the associated retry timestamp, effectively removing any restrictions on when the next generation request can be made for the specified key. It is typically called after a successful generation or when the backoff period has elapsed.
    /// </summary>
    /// <param name="key">The key identifying the resource generation request for which the backoff should be cleared.</param>
    private void ClearFailedGenerationBackoff(ResourceGenerationKey key)
    {
        if (!_lastFailedGenerationKey.HasValue || _lastFailedGenerationKey.Value != key)
            return;

        _lastFailedGenerationKey = null;
        _failedGenerationRetryAfterTimestamp = 0;
    }

    /// <summary>
    /// Discards the currently pending resource generation, if any, marking it as superseded and disposing of its resources. This method is called when a new generation request is made that supersedes the existing pending generation, or when the pending generation is no longer valid due to changes in the rendering context. It logs a message indicating that the pending generation has been discarded, along with details about the pipeline, reason for discarding, and the keys of the pending and active generations.
    /// </summary>
    /// <param name="reason">The reason for discarding the pending generation.</param>
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

    /// <summary>
    /// Commits the currently pending resource generation, making it the active generation and retiring any previous active generation. This method checks if the pending generation is ready and valid, and if so, it updates the active generation reference, clears the pending generation, and notifies any listeners of the change in render resources. It also logs detailed information about the committed generation, including its key, resource counts, and build duration.
    /// </summary>
    /// <param name="reason">The reason for committing the pending generation.</param>
    private bool CommitPendingGeneration(string reason)
    {
        RenderResourceGeneration? pending = PendingGeneration;
        if (pending is null || !pending.IsReady)
            return false;

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
            return false;
        }

        XRViewport? viewport = RenderState.WindowViewport ?? LastWindowViewport;
        IRenderResourceGenerationBackend? backend = ResourceGenerationBackendOverride ?? AbstractRenderer.Current;
        IRenderResourceGenerationTransaction? backendTransaction = null;
        if (backend is not null &&
            !backend.TryPrepareRenderResourceGeneration(
                this,
                pending,
                viewport,
                out backendTransaction,
                out string? backendFailureReason))
        {
            string failure = string.IsNullOrWhiteSpace(backendFailureReason)
                ? "Backend resource generation preparation failed."
                : backendFailureReason;
            RegisterPendingGenerationFailure(pending.Key, failure);
            PendingGeneration = null;
            ClearPendingGenerationDebounce();
            pending.MarkFailed(failure);
            pending.Dispose();
            return false;
        }

        using (backendTransaction)
        {
            RenderResourceGeneration? old = ActiveGeneration;
            ActiveGeneration = pending;
            PendingGeneration = null;
            ActiveGeneration.MarkActive(reason);
            ResourceGeneration++;

            try
            {
                backendTransaction?.Commit();
            }
            catch (Exception ex)
            {
                ResourceGeneration--;
                ActiveGeneration = old;
                string failure = $"Backend resource generation commit failed: {ex.Message}";
                RegisterPendingGenerationFailure(pending.Key, failure);
                ClearPendingGenerationDebounce();
                pending.MarkFailed(failure);
                pending.Dispose();
                return false;
            }

            ClearPendingGenerationDebounce();
            ClearFailedGenerationBackoff(ActiveGeneration.Key);

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

        return true;
    }

    /// <summary>
    /// Attempts to build the current resource generation key based on the active or last known viewport. If a valid viewport is available, it resolves the necessary dimensions and constructs a resource generation key that encapsulates the rendering parameters for the current frame. This method is used to determine whether existing resources can be reused or if new resources need to be generated.
    /// </summary>
    /// <param name="key">The constructed resource generation key if a valid viewport is available; otherwise, the default value.</param>
    /// <returns>True if a valid resource generation key was built; otherwise, false.</returns>
    private bool TryBuildCurrentViewportGenerationKey(out ResourceGenerationKey key)
    {
        XRViewport? viewport = RenderState.WindowViewport ?? LastWindowViewport;
        if (viewport is null)
        {
            key = default;
            return false;
        }

        var dimensions = ResolveViewportResourceDimensions(viewport);
        key = BuildResourceGenerationKey(
            dimensions.DisplayWidth,
            dimensions.DisplayHeight,
            dimensions.InternalWidth,
            dimensions.InternalHeight,
            viewport);
        return true;
    }

    /// <summary>
    /// Retires a previously active resource generation, marking it as retired and enqueueing it for disposal. This method is called when a new resource generation is committed, and the old generation is no longer needed. It logs detailed information about the retired generation, including its key, resource counts, and the current size of the retired generations queue. If the queue exceeds the maximum allowed size, it will dispose of the oldest retired generation to free up resources.
    /// </summary>
    /// <param name="generation">The resource generation to retire.</param>
    /// <param name="reason">The reason for retiring the generation.</param>
    private void RetireGeneration(RenderResourceGeneration generation, string reason)
    {
        generation.MarkRetired(reason);
        generation.ArmRetirementFence(AbstractRenderer.Current?.InsertGpuFence());
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

        DrainRetiredGenerations();
        if (_retiredGenerations.Count > MaxRetiredResourceGenerations)
        {
            WaitForGpuBeforePhysicalResourceDestruction("RetiredRenderResourceGenerationCap");
            DrainRetiredGenerations(force: true);
        }
    }

    private void DrainRetiredGenerations(bool force = false)
    {
        while (_retiredGenerations.Count != 0)
        {
            RenderResourceGeneration retired = _retiredGenerations.Peek();
            EGpuFenceStatus fenceStatus = retired.PollRetirementFence();
            if (!force && fenceStatus == EGpuFenceStatus.Pending)
                return;

            if (fenceStatus == EGpuFenceStatus.Failed)
            {
                Debug.RenderingWarning(
                    "[RenderResources] Retired generation fence failed. Pipeline={0} Key={1} Force={2}",
                    ProfilerKey,
                    retired.Key,
                    force);
                if (!force)
                    return;
            }

            _retiredGenerations.Dequeue();
            retired.Dispose();
            Debug.Rendering(
                "[RenderResources] Retired generation disposed. Pipeline={0} Key={1} Fence={2} Force={3} RemainingQueue={4}",
                ProfilerKey,
                retired.Key,
                fenceStatus,
                force,
                _retiredGenerations.Count);

            if (force && _retiredGenerations.Count <= MaxRetiredResourceGenerations)
                return;
        }
    }

    /// <summary>
    /// Destroys all cached GPU resources and resets the resource generation state. 
    /// This method ensures that all active, pending, and retired resource generations are disposed of, 
    /// and that any legacy resources are also destroyed. 
    /// It is typically called when the rendering context is being reset 
    /// or when a significant change in rendering parameters requires a complete rebuild of resources. 
    /// If called from a non-render thread, it enqueues the destruction to be performed on the render thread to ensure thread safety.
    /// </summary>
    public void DestroyCache()
    {
        if (!RuntimeEngine.IsRenderThread)
        {
            EnqueueDestroyCache();
            return;
        }

        DestroyCacheOnRenderThread();
    }

    /// <summary>
    /// Destroys the cached GPU resources if any tracked resources exist. 
    /// This method checks for the presence of active, pending, or retired resource generations,
    /// and only destroys the cache if such resources exist.
    /// </summary>
    private void DestroyCacheIfResourcesExist()
    {
        if (!HasAnyTrackedResources())
            return;

        DestroyCache();
    }

    /// <summary>
    /// Enqueues a task to destroy cached GPU resources on the render thread. 
    /// This method ensures that the destruction of resources is performed in a thread-safe manner.
    /// </summary>
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

    /// <summary>
    /// Destroys cached GPU resources on the render thread, ensuring that all active, pending, and retired resource generations are disposed of.
    /// It also destroys any legacy resources that may still be present. 
    /// This method is called from the render thread to ensure that resource destruction is performed safely and without interfering with ongoing rendering operations.
    /// </summary>
    private void DestroyCacheOnRenderThread()
    {
        if (!HasAnyTrackedResources())
            return;

        NotifyPipelineResourcesDestroyed("DestroyCache");
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
        if (viewport is not null)
        {
            var dimensions = ResolveViewportResourceDimensions(viewport);
            if (RequestResourceGeneration(
                dimensions.DisplayWidth,
                dimensions.DisplayHeight,
                dimensions.InternalWidth,
                dimensions.InternalHeight,
                "InvalidatePhysicalResources",
                force: true,
                viewport: viewport))
            {
                return;
            }
        }

        NotifyPipelineResourcesDestroyed($"InvalidatePhysicalResources (generation {ResourceGeneration} -> {ResourceGeneration + 1})");
        WaitForGpuBeforePhysicalResourceDestruction("InvalidatePhysicalResources");
        Resources.DestroyAllPhysicalResources(retainDescriptors: true);
        ResourceGeneration++;
    }

    /// <summary>
    /// Checks if there are any tracked resources in the render pipeline instance, including active, pending, retired generations, and legacy resources.
    /// </summary>
    /// <returns>True if there are any tracked resources; otherwise, false.</returns>
    private bool HasAnyTrackedResources()
        => ActiveGeneration is not null
        || PendingGeneration is not null
        || _retiredGenerations.Count != 0
        || _legacyResources.TextureRecords.Count != 0
        || _legacyResources.FrameBufferRecords.Count != 0
        || _legacyResources.BufferRecords.Count != 0
        || _legacyResources.RenderBufferRecords.Count != 0;

    /// <summary>
    /// Removes a texture resource by name, ensuring that any associated GPU resources are properly destroyed and that the render pipeline is notified of the destruction.
    /// </summary>
    /// <param name="name">The name of the texture resource to remove.</param>
    /// <param name="reason">The reason for removing the texture resource.</param>
    internal void RemoveTextureResource(string name, string reason)
    {
        if (EnqueueResourceMutationIfOffRenderThread(() => RemoveTextureResource(name, reason), $"XRRenderPipelineInstance.RemoveTextureResource[{name}]"))
            return;

        if (Resources.TryGetTexture(name, out XRTexture? texture) && texture is not null)
            _pipeline?.OnTextureDestroyed(this, name, texture, reason);

        WaitForGpuBeforePhysicalResourceDestruction($"RemoveTextureResource[{name}]");
        Resources.RemoveTexture(name);
    }

    /// <summary>
    /// Removes a framebuffer resource by name, ensuring that any associated GPU resources are properly destroyed and that the render pipeline is notified of the destruction.
    /// </summary>
    /// <param name="name">The name of the framebuffer resource to remove.</param>
    /// <param name="reason">The reason for removing the framebuffer resource.</param>
    internal void RemoveFrameBufferResource(string name, string reason)
    {
        if (EnqueueResourceMutationIfOffRenderThread(() => RemoveFrameBufferResource(name, reason), $"XRRenderPipelineInstance.RemoveFrameBufferResource[{name}]"))
            return;

        if (Resources.TryGetFrameBuffer(name, out XRFrameBuffer? frameBuffer) && frameBuffer is not null)
            _pipeline?.OnFrameBufferDestroyed(this, name, frameBuffer, reason);

        WaitForGpuBeforePhysicalResourceDestruction($"RemoveFrameBufferResource[{name}]");
        Resources.RemoveFrameBuffer(name);
    }

    /// <summary>
    /// Waits for the GPU to finish all work before destroying physical resources. 
    /// This is necessary to avoid destroying resources that are still in use by the GPU, 
    /// which can lead to undefined behavior or crashes. 
    /// The reason parameter is used for logging purposes to indicate why the wait is being performed.
    /// </summary>
    /// <param name="reason">The reason for waiting for the GPU.</param>
    private static void WaitForGpuBeforePhysicalResourceDestruction(string reason)
    {
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null)
            return;

        renderer.WaitForGpu();
        if (renderer is not VulkanRenderer vulkanRenderer)
            return;

        if (vulkanRenderer.IsDeviceLost)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.RenderPipeline.ResourceDestroy.DeviceLost.{reason}",
                System.TimeSpan.FromSeconds(1),
                "[Vulkan] Skipping descriptor-reference release after GPU wait reported device loss: {0}",
                reason);
            return;
        }

        vulkanRenderer.ReleaseDescriptorReferencesForPhysicalResourceDestruction(reason);
        Debug.VulkanEvery(
            $"Vulkan.RenderPipeline.ResourceDestroy.WaitIdle.{reason}",
            System.TimeSpan.FromSeconds(1),
            "[Vulkan] GPU wait before render-pipeline physical resource destruction: {0}",
            reason);
    }

    /// <summary>
    /// Called when the viewport is resized. 
    /// This method updates the render pipeline instance to handle the new viewport size.
    /// </summary>
    /// <param name="size">The new size of the viewport.</param>
    public void ViewportResized(Vector2 size)
        => ViewportResized((int)size.X, (int)size.Y);

    /// <summary>
    /// Called when the viewport is resized.
    /// This method updates the render pipeline instance to handle the new viewport size.
    /// </summary>
    /// <param name="size">The new size of the viewport.</param>
    /// <param name="viewport">The viewport that owns the resize request, when known.</param>
    public void ViewportResized(Vector2 size, XRViewport? viewport)
        => ViewportResized((int)size.X, (int)size.Y, viewport);
    /// <summary>
    /// Called when the viewport is resized.
    /// This method updates the render pipeline instance to handle the new viewport size.
    /// </summary>
    /// <param name="width">The new width of the viewport.</param>
    /// <param name="height">The new height of the viewport.</param>
    public void ViewportResized(int width, int height)
        => ViewportResized(width, height, null);

    /// <summary>
    /// Called when the viewport is resized.
    /// This method updates the render pipeline instance to handle the new viewport size.
    /// </summary>
    /// <param name="width">The new width of the viewport.</param>
    /// <param name="height">The new height of the viewport.</param>
    /// <param name="viewport">The viewport that owns the resize request, when known.</param>
    public void ViewportResized(int width, int height, XRViewport? viewport)
    {
        _pipeline?.HandleViewportResized(this, width, height, viewport);
    }

    /// <summary>
    /// Called when the internal resolution is resized.
    /// This method updates the render pipeline instance to handle the new internal resolution.
    /// </summary>
    /// <param name="internalWidth">The new internal width.</param>
    /// <param name="internalHeight">The new internal height.</param>
    public void InternalResolutionResized(int internalWidth, int internalHeight)
        => InternalResolutionResized(internalWidth, internalHeight, null);

    /// <summary>
    /// Called when the internal resolution is resized.
    /// This method updates the render pipeline instance to handle the new internal resolution.
    /// </summary>
    /// <param name="internalWidth">The new internal width.</param>
    /// <param name="internalHeight">The new internal height.</param>
    /// <param name="viewport">The viewport that owns the resize request, when known.</param>
    public void InternalResolutionResized(int internalWidth, int internalHeight, XRViewport? viewport)
    {
        viewport ??= RenderState.WindowViewport ?? LastWindowViewport;
        var dimensions = ResolveViewportResizeResourceDimensions(
            viewport,
            viewport?.Width ?? internalWidth,
            viewport?.Height ?? internalHeight,
            internalWidth,
            internalHeight);

        if (RequestResourceGeneration(
            dimensions.DisplayWidth,
            dimensions.DisplayHeight,
            dimensions.InternalWidth,
            dimensions.InternalHeight,
            "InternalResolutionResized",
            viewport: viewport))
            return;

        InvalidatePhysicalResources();
    }

    /// <summary>
    /// Gets a texture resource by name and casts it to the specified type T.
    /// </summary>
    /// <typeparam name="T">The type of the texture resource.</typeparam>
    /// <param name="name">The name of the texture resource.</param>
    /// <returns>The texture resource cast to the specified type, or null if not found.</returns>
    public T? GetTexture<T>(string name) where T : XRTexture
    {
        if (TryGetTexture(name, out XRTexture? value))
            return value as T;
        return null;
    }

    /// <summary>
    /// Tries to get a texture resource by name.
    /// If found, it outputs the texture and returns true; otherwise, it returns false.
    /// </summary>
    /// <param name="name">The name of the texture resource.</param>
    /// <param name="texture">The output texture resource if found.</param>
    /// <returns>True if the texture resource is found; otherwise, false.</returns>
    public bool TryGetTexture(string name, out XRTexture? texture)
    {
        return Resources.TryGetTexture(name, out texture)
            || Variables.TryResolveTexture(Resources, name, out texture);
    }

    /// <summary>
    /// Sets a texture resource in the render pipeline instance.
    /// </summary>
    /// <param name="texture">The texture resource to set.</param>
    /// <param name="descriptor">An optional descriptor for the texture resource.</param>
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

        if (!ReferenceEquals(existingTexture, texture))
            _pipeline?.OnTextureBound(this, name, texture, existingTexture);

        bool bindingChanged = !ReferenceEquals(existingTexture, texture);
        Resources.BindTexture(texture, descriptor);
        if (bindingChanged)
            NotifyRenderResourcesChanged();
    }

    /// <summary>
    /// Gets a data buffer resource by name.
    /// </summary>
    /// <param name="name">The name of the data buffer resource.</param>
    /// <returns>The data buffer resource if found; otherwise, null.</returns>
    public XRDataBuffer? GetBuffer(string name)
    {
        if (TryGetBuffer(name, out XRDataBuffer? value))
            return value;
        return null;
    }

    /// <summary>
    /// Tries to get a data buffer resource by name.
    /// If found, it outputs the buffer and returns true; otherwise, it returns false.
    /// </summary>
    /// <param name="name">The name of the data buffer resource.</param>
    /// <param name="buffer">The output data buffer resource if found.</param>
    /// <returns>True if the data buffer resource is found; otherwise, false.</returns>
    public bool TryGetBuffer(string name, out XRDataBuffer? buffer) 
        => Resources.TryGetBuffer(name, out buffer)
            || Variables.TryResolveBuffer(Resources, name, out buffer);

    /// <summary>
    /// Sets a data buffer resource in the render pipeline instance.
    /// </summary>
    /// <param name="buffer">The data buffer resource to set.</param>
    /// <param name="descriptor">An optional descriptor for the data buffer resource.</param>
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

    internal void BindImportedTexture(XRTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        string name = texture.Name ?? throw new InvalidOperationException("Imported texture name must be set before binding.");
        RenderResourceRegistry registry = GetImportedResourceRegistry(name, ExternalRenderResourceKind.Texture);

        registry.TryGetTexture(name, out XRTexture? existingTexture);
        TextureResourceDescriptor descriptor = RenderResourceDescriptorFactory.FromTexture(texture) with
        {
            Name = name,
            Lifetime = RenderResourceLifetime.External,
        };
        registry.BindTexture(texture, descriptor, ownsInstance: false);
        if (!ReferenceEquals(existingTexture, texture))
            NotifyRenderResourcesChanged();
    }

    internal void BindImportedBuffer(XRDataBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        string name = buffer.AttributeName;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Imported buffer attribute name must be set before binding.");

        RenderResourceRegistry registry = GetImportedResourceRegistry(name, ExternalRenderResourceKind.Buffer);
        registry.TryGetBuffer(name, out XRDataBuffer? existingBuffer);
        BufferResourceDescriptor descriptor = RenderResourceDescriptorFactory.FromBuffer(buffer) with
        {
            Name = name,
            Lifetime = RenderResourceLifetime.External,
        };
        registry.BindBuffer(buffer, descriptor, ownsInstance: false);
        if (!ReferenceEquals(existingBuffer, buffer))
            NotifyRenderResourcesChanged();
    }

    internal bool UnbindImportedTexture(string name)
    {
        if (!TryGetImportedResourceRegistry(name, ExternalRenderResourceKind.Texture, out RenderResourceRegistry? registry))
            return false;
        if (!registry.TryGetTexture(name, out _))
            return false;

        registry.RemoveTexture(name);
        NotifyRenderResourcesChanged();
        return true;
    }

    internal bool UnbindImportedBuffer(string name)
    {
        if (!TryGetImportedResourceRegistry(name, ExternalRenderResourceKind.Buffer, out RenderResourceRegistry? registry))
            return false;
        if (!registry.TryGetBuffer(name, out _))
            return false;

        registry.RemoveBuffer(name);
        NotifyRenderResourcesChanged();
        return true;
    }

    private RenderResourceRegistry GetImportedResourceRegistry(string name, ExternalRenderResourceKind expectedKind)
    {
        if (TryGetImportedResourceRegistry(name, expectedKind, out RenderResourceRegistry? registry))
            return registry;

        RenderResourceGeneration? generation = ActiveGeneration;
        string actualContract;
        if (generation is null)
        {
            actualContract = "<no active generation>";
        }
        else if (!generation.Layout.ResourcesByName.TryGetValue(name, out RenderPipelineResourceSpec? spec))
        {
            actualContract = "<undeclared>";
        }
        else if (spec is ExternalResourceSpec external)
        {
            actualContract = $"ExternalKind={external.ExternalKind},Ownership={external.Ownership},Synchronization={external.Synchronization}";
        }
        else
        {
            actualContract = $"Kind={spec.Kind},Lifetime={spec.Lifetime}";
        }

        throw new InvalidOperationException(
            $"Imported resource mismatch. Pipeline={ProfilerKey} Generation={generation?.Key.ToString() ?? "<none>"} " +
            $"Resource={name} ExpectedExternalKind={expectedKind} Actual={actualContract}.");
    }

    private bool TryGetImportedResourceRegistry(
        string name,
        ExternalRenderResourceKind expectedKind,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RenderResourceRegistry? registry)
    {
        RenderResourceGeneration? generation = ActiveGeneration;
        if (generation is not null &&
            generation.Layout.ResourcesByName.TryGetValue(name, out RenderPipelineResourceSpec? spec) &&
            spec is ExternalResourceSpec external &&
            external.ExternalKind == expectedKind)
        {
            registry = generation.Registry;
            return true;
        }

        registry = null;
        return false;
    }

    /// <summary>
    /// Gets a frame buffer resource by name and casts it to the specified type T.
    /// </summary>
    /// <typeparam name="T">The type to cast the frame buffer resource to.</typeparam>
    /// <param name="name">The name of the frame buffer resource.</param>
    /// <returns>The frame buffer resource cast to the specified type, or null if not found or cast fails.</returns>
    public T? GetFBO<T>(string name) where T : XRFrameBuffer 
        => TryGetFBO(name, out XRFrameBuffer? value) ? value as T : null;

    /// <summary>
    /// Tries to get a frame buffer resource by name.
    /// If found, it outputs the frame buffer and returns true; otherwise, it returns false
    /// </summary>
    /// <param name="name">The name of the frame buffer resource.</param>
    /// <param name="fbo">The output frame buffer resource if found; otherwise, null.</param>
    /// <returns>True if the frame buffer resource is found; otherwise, false.</returns>
    public bool TryGetFBO(string name, out XRFrameBuffer? fbo)
        => Resources.TryGetFrameBuffer(name, out fbo)
            || Variables.TryResolveFrameBuffer(Resources, name, out fbo);

    /// <summary>
    /// Sets a frame buffer resource in the render pipeline instance.
    /// </summary>
    /// <param name="fbo">The frame buffer resource to set.</param>
    /// <param name="descriptor">An optional descriptor for the frame buffer resource.</param>
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

        if (!ReferenceEquals(existingFbo, fbo))
            _pipeline?.OnFrameBufferBound(this, name, fbo, existingFbo);

        bool bindingChanged = !ReferenceEquals(existingFbo, fbo);
        Resources.BindFrameBuffer(fbo, descriptor);
        if (bindingChanged)
            NotifyRenderResourcesChanged();
    }

    /// <summary>
    /// Notifies the current renderer that the render resources have changed, prompting it to update its state accordingly.
    /// </summary>
    internal void NotifyRenderResourcesChanged([CallerMemberName] string? reason = null)
        => AbstractRenderer.Current?.NotifyRenderResourcesChanged(DescribeRenderResourceChangeReason(reason));

    /// <summary>
    /// Provides a human-readable description of the reason for a render resource change, based on the caller member name. 
    /// This method maps specific method names to more descriptive strings for logging and debugging purposes. 
    /// If the reason is null or empty, it defaults to the name of the NotifyRenderResourcesChanged method.
    /// </summary>
    /// <param name="reason">The caller member name indicating the reason for the render resource change.</param>
    /// <returns>A human-readable description of the reason for the render resource change.</returns>
    private static string DescribeRenderResourceChangeReason(string? reason)
        => reason switch
        {
            nameof(CommitPendingGeneration) => "XRRenderPipelineInstance.CommitPendingGeneration",
            nameof(SetTexture) => "XRRenderPipelineInstance.SetTexture",
            nameof(SetBuffer) => "XRRenderPipelineInstance.SetBuffer",
            nameof(SetFBO) => "XRRenderPipelineInstance.SetFBO",
            nameof(SetRenderBuffer) => "XRRenderPipelineInstance.SetRenderBuffer",
            null or "" => nameof(NotifyRenderResourcesChanged),
            _ => reason
        };

    /// <summary>
    /// Notifies the active render pipeline before destroying resources, including textures, frame buffers, buffers, and render buffers.
    /// </summary>
    /// <param name="reason">The reason for the destruction of the resources.</param>
    private void NotifyPipelineResourcesDestroyed(string reason)
    {
        RenderPipeline? pipeline = _pipeline;
        if (pipeline is null)
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
            pipeline.OnTextureDestroyed(this, texture.Name ?? "<unnamed>", texture, reason);
        }

        foreach (var record in Resources.FrameBufferRecords.Values)
        {
            if (record.Instance is not XRFrameBuffer frameBuffer)
                continue;

            liveFrameBufferCount++;
            pipeline.OnFrameBufferDestroyed(this, frameBuffer.Name ?? "<unnamed>", frameBuffer, reason);
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

    /// <summary>
    /// Gets a render buffer resource by name.
    /// </summary>
    /// <param name="name">The name of the render buffer resource.</param>
    /// <returns>The render buffer resource if found; otherwise, null.</returns>
    public XRRenderBuffer? GetRenderBuffer(string name)
        => TryGetRenderBuffer(name, out XRRenderBuffer? value) ? value : null;

    /// <summary>
    /// Tries to get a render buffer resource by name.
    /// </summary>
    /// <param name="name">The name of the render buffer resource.</param>
    /// <param name="renderBuffer">When this method returns, contains the render buffer resource if found; otherwise, null.</param>
    /// <returns>True if the render buffer resource was found; otherwise, false.</returns>
    public bool TryGetRenderBuffer(string name, out XRRenderBuffer? renderBuffer)
        => Resources.TryGetRenderBuffer(name, out renderBuffer)
            || Variables.TryResolveRenderBuffer(Resources, name, out renderBuffer);

    /// <summary>
    /// Sets a render buffer resource in the render pipeline instance.
    /// </summary>
    /// <param name="renderBuffer">The render buffer resource to set.</param>
    /// <param name="descriptor">An optional descriptor for the render buffer resource.</param>
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

    /// <summary>
    /// Validates that the descriptors of the active generation match the expected specifications.
    /// </summary>
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

    /// <summary>
    /// Validates that the texture descriptor for a given texture specification matches the actual descriptor in the registry.
    /// </summary>
    /// <param name="generation">The render resource generation.</param>
    /// <param name="registry">The render resource registry.</param>
    /// <param name="spec">The texture specification.</param>
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
            WarnDescriptorMismatch(generation, spec.Name, "texture", DescribeTextureDescriptor(expected), DescribeTextureDescriptor(actual));
    }

    /// <summary>
    /// Validates that the texture view descriptor for a given texture view specification matches the actual descriptor in the registry.
    /// </summary>
    /// <param name="generation">The render resource generation.</param>
    /// <param name="registry">The render resource registry.</param>
    /// <param name="spec">The texture view specification.</param>
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
            WarnDescriptorMismatch(generation, spec.Name, "texture view", DescribeTextureDescriptor(expected), DescribeTextureDescriptor(actual));
    }

    /// <summary>
    /// Validates that the frame buffer descriptor for a given frame buffer specification matches the actual descriptor in the registry.
    /// </summary>
    /// <param name="generation">The render resource generation.</param>
    /// <param name="registry">The render resource registry.</param>
    /// <param name="spec">The frame buffer specification.</param>
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
            WarnDescriptorMismatch(generation, spec.Name, "framebuffer", DescribeFrameBufferDescriptor(expected), DescribeFrameBufferDescriptor(actual));
    }

    /// <summary>
    /// Validates that the render buffer descriptor for a given render buffer specification matches the actual descriptor in the registry.
    /// </summary>
    /// <param name="generation">The render resource generation.</param>
    /// <param name="registry">The render resource registry.</param>
    /// <param name="spec">The render buffer specification.</param>
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
            WarnDescriptorMismatch(generation, spec.Name, "renderbuffer", expected.ToString(), actual.ToString());
    }

    /// <summary>
    /// Validates that the buffer descriptor for a given buffer specification matches the actual descriptor in the registry.
    /// </summary>
    /// <param name="generation">The render resource generation.</param>
    /// <param name="registry">The render resource registry.</param>
    /// <param name="spec">The buffer specification.</param>
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
            WarnDescriptorMismatch(generation, spec.Name, "buffer", expected.ToString(), actual.ToString());
    }

    /// <summary>
    /// Logs a warning if a declared resource is missing after frame execution. This is used to notify developers that a required resource was not created or bound as expected, which may lead to rendering issues.
    /// </summary>
    /// <param name="generation">The render resource generation.</param>
    /// <param name="spec">The resource specification.</param>
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

    /// <summary>
    /// Logs a warning if there is a mismatch between the expected and actual descriptors for a resource. This helps identify issues where the resource was created or bound with different parameters than what was declared in the pipeline specification.
    /// </summary>
    /// <param name="generation">The render resource generation.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="kind">The kind of the resource.</param>
    /// <param name="expected">The expected descriptor.</param>
    /// <param name="actual">The actual descriptor.</param>
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

    /// <summary>
    /// Describes a texture resource descriptor in a human-readable format for logging and debugging purposes. This method constructs a string representation of the descriptor's properties, including lifetime, size policy, format label, stereo compatibility, array layers, aliasing support, and storage usage requirements.
    /// </summary>
    /// <param name="descriptor">The texture resource descriptor.</param>
    /// <returns>A string representation of the texture resource descriptor.</returns>
    private static string DescribeTextureDescriptor(TextureResourceDescriptor descriptor)
        => $"lifetime={descriptor.Lifetime},size={descriptor.SizePolicy},format={descriptor.FormatLabel},stereo={descriptor.StereoCompatible},layers={descriptor.ArrayLayers},alias={descriptor.SupportsAliasing},storage={descriptor.RequiresStorageUsage}";

    /// <summary>
    /// Describes a frame buffer resource descriptor in a human-readable format for logging and debugging purposes. This method constructs a string representation of the descriptor's properties, including lifetime, size policy, and attachments.
    /// </summary>
    /// <param name="descriptor">The frame buffer resource descriptor.</param>
    /// <returns>A string representation of the frame buffer resource descriptor.</returns>
    private static string DescribeFrameBufferDescriptor(FrameBufferResourceDescriptor descriptor)
        => $"lifetime={descriptor.Lifetime},size={descriptor.SizePolicy},attachments={DescribeAttachments(descriptor.Attachments)}";

    /// <summary>
    /// Describes a list of frame buffer attachment descriptors in a human-readable format for logging and debugging purposes. This method constructs a string representation of each attachment's properties, including attachment type, resource name, mip level, and layer index, and combines them into a single string.
    /// </summary>
    /// <param name="attachments">The list of frame buffer attachment descriptors.</param>
    /// <returns>A string representation of the list of frame buffer attachment descriptors.</returns>
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

    /// <summary>
    /// Compares two lists of frame buffer attachment descriptors for equality. This method checks if the two lists have the same count and if each corresponding attachment descriptor is equal. It is used to validate that the expected and actual frame buffer attachments match in the render pipeline.
    /// </summary>
    /// <param name="left">The first list of frame buffer attachment descriptors.</param>
    /// <param name="right">The second list of frame buffer attachment descriptors.</param>
    /// <returns>True if the lists are equal; otherwise, false.</returns>
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

    /// <summary>
    /// Logs a warning if a screen-space UI is provided to the render pipeline but no corresponding render command exists in the command chain. This indicates that the UI will not be rendered through the pipeline, which may lead to unexpected behavior or missing UI elements.
    /// </summary>
    /// <param name="userInterface">The screen-space UI instance.</param>
    /// <param name="viewport">The viewport associated with the UI.</param>
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

    /// <summary>
    /// Checks if the given viewport render command container contains a command for rendering screen-space UI. This method recursively checks through the command chain, including any conditional or switch commands, to determine if a VPRC_RenderScreenSpaceUI command is present.
    /// </summary>
    /// <param name="container">The viewport render command container to check.</param>
    /// <returns>True if a VPRC_RenderScreenSpaceUI command is found; otherwise, false.</returns>
    internal static bool ContainsScreenSpaceUiRenderCommand(ViewportRenderCommandContainer container)
    {
        foreach (var cmd in container)
        {
            switch (cmd)
            {
                case VPRC_RenderScreenSpaceUI:
                    return true;
                case VPRC_IfElse ifElse:
                {
                    if (ifElse.TrueCommands is not null && ContainsScreenSpaceUiRenderCommand(ifElse.TrueCommands))
                        return true;
                    if (ifElse.FalseCommands is not null && ContainsScreenSpaceUiRenderCommand(ifElse.FalseCommands))
                        return true;
                    break;
                }
                case VPRC_Switch switchCmd:
                {
                    if (switchCmd.Cases is not null)
                        foreach (var caseContainer in switchCmd.Cases.Values)
                            if (ContainsScreenSpaceUiRenderCommand(caseContainer))
                                return true;

                    if (switchCmd.DefaultCase is not null && ContainsScreenSpaceUiRenderCommand(switchCmd.DefaultCase))
                        return true;

                    break;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Registers the index of a render graph pass that has been executed during the current frame. This method is used for validation purposes to ensure that all executed passes have corresponding metadata in the render pipeline. If the pass index is valid (not int.MinValue), it adds the index to the set of executed pass indices and, if within a branch scope, also adds it to the set of branch-executed pass indices.
    /// </summary>
    /// <param name="passIndex">The index of the render graph pass that has been executed.</param>
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

    /// <summary>
    /// Pushes a new render graph branch scope onto the stack, incrementing the active branch depth. This is used to track nested branches in the render graph for validation purposes. When the returned StateObject is disposed, it will decrement the active branch depth, ensuring proper tracking of nested scopes.
    /// </summary>
    /// <returns>A StateObject representing the branch scope. Disposing this object will decrement the active branch depth.</returns>
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

    /// <summary>
    /// Begins a new frame for render graph validation, 
    /// clearing the sets of executed pass indices and 
    /// resetting the active branch depth to zero. 
    /// This method is called at the start of each frame 
    /// to prepare for tracking executed render graph passes 
    /// and their corresponding metadata validation.
    /// </summary>
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
