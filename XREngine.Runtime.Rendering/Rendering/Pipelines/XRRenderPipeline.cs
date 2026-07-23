using MemoryPack;
using System;
using System.ComponentModel;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using YamlDotNet.Serialization;
using static XREngine.Rendering.XRRenderPipelineInstance;

namespace XREngine.Rendering;

/// <summary>
/// Base class for all render pipelines. 
/// A render pipeline is a collection of rendering commands and resources 
/// that define how a scene is rendered to a viewport. 
/// Render pipelines can be customized and extended 
/// to support different rendering techniques, post-processing effects, 
/// and resource management strategies.
/// </summary>
[XRAssetInspector("XREngine.Editor.AssetEditors.RenderPipelineInspector, XREngine.Editor")]
[MemoryPackable(GenerateType.NoGenerate)]
public abstract partial class RenderPipeline : XRAsset, IRuntimeRenderPipelineHost
{
    /// <summary>
    /// Synchronization object for OpenXR pipeline factory registration and creation.
    /// </summary>
    private static readonly object OpenXrFactorySync = new();
    /// <summary>
    /// Mapping of source pipeline types to factory functions that create OpenXR-compatible pipelines.
    /// </summary>
    private static readonly Dictionary<Type, Func<RenderPipeline, RenderPipeline>> OpenXrPipelineFactories = [];

    /// <summary>
    /// Registers a factory function that creates an OpenXR-compatible render pipeline from a source pipeline type.
    /// </summary>
    /// <typeparam name="TPipeline">The type of the source pipeline.</typeparam>
    /// <param name="factory">The factory function that creates the OpenXR-compatible pipeline.</param>
    public static void RegisterOpenXrPipelineFactory<TPipeline>(Func<TPipeline, RenderPipeline> factory)
        where TPipeline : RenderPipeline
    {
        ArgumentNullException.ThrowIfNull(factory);
        RegisterOpenXrPipelineFactory(typeof(TPipeline), source => factory((TPipeline)source));
    }

    /// <summary>
    /// Registers a factory function that creates an OpenXR-compatible render pipeline from a source pipeline type.
    /// </summary>
    /// <param name="pipelineType">The type of the source pipeline.</param>
    /// <param name="factory">The factory function that creates the OpenXR-compatible pipeline.</param>
    /// <exception cref="ArgumentException">Thrown if the pipelineType does not derive from RenderPipeline.</exception>
    public static void RegisterOpenXrPipelineFactory(Type pipelineType, Func<RenderPipeline, RenderPipeline> factory)
    {
        ArgumentNullException.ThrowIfNull(pipelineType);
        ArgumentNullException.ThrowIfNull(factory);

        if (!typeof(RenderPipeline).IsAssignableFrom(pipelineType))
            throw new ArgumentException($"Type must derive from {nameof(RenderPipeline)}.", nameof(pipelineType));

        lock (OpenXrFactorySync)
            OpenXrPipelineFactories[pipelineType] = factory;
    }

    /// <summary>
    /// Attempts to create an OpenXR-compatible render pipeline from a source pipeline instance.
    /// </summary>
    /// <param name="sourcePipeline">The source pipeline instance.</param>
    /// <param name="pipeline">The created OpenXR-compatible pipeline, if successful.</param>
    /// <returns>True if the OpenXR-compatible pipeline was created successfully; otherwise, false.</returns>
    public static bool TryCreateOpenXrPipeline(RenderPipeline sourcePipeline, out RenderPipeline? pipeline)
    {
        ArgumentNullException.ThrowIfNull(sourcePipeline);

        Func<RenderPipeline, RenderPipeline>? factory;
        lock (OpenXrFactorySync)
            OpenXrPipelineFactories.TryGetValue(sourcePipeline.GetType(), out factory);

        pipeline = factory?.Invoke(sourcePipeline);
        return pipeline is not null;
    }

    /// <summary>
    /// Gets the list of active render pipeline instances that are currently using this pipeline.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public List<XRRenderPipelineInstance> Instances { get; } = [];

    /// <summary>
    /// When true, <see cref="RuntimeEngine.Rendering.ApplyRenderPipelinePreference"/> will not replace
    /// this pipeline with the global debug/default preference. Use this for viewports that
    /// require a specific pipeline (e.g., VR desktop mirror cameras that need deferred rendering).
    /// </summary>
    [Browsable(false)]
    public bool OverrideProtected { get; set; }

    /// <summary>
    /// Gets the invalid material for this pipeline.
    /// </summary>
    protected abstract Lazy<XRMaterial> InvalidMaterialFactory { get; }

    /// <summary>
    /// Gets the invalid material for this pipeline.
    /// This material is used when a render command references a missing or invalid material.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public XRMaterial InvalidMaterial
        => InvalidMaterialFactory.Value;

    private RenderPipelinePostProcessSchema _postProcessSchema = RenderPipelinePostProcessSchema.Empty;

    /// <summary>
    /// Structured description of the post-processing controls exposed by this pipeline.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public RenderPipelinePostProcessSchema PostProcessSchema => _postProcessSchema;

    /// <summary>
    /// Human readable identifier for debug output.
    /// Derived pipelines can override this to expose a friendlier label.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public virtual string DebugName => GetType().Name;

    private bool _isShadowPass;
    /// <summary>
    /// Indicates whether the current render pass is a shadow pass. 
    /// This property can be used by derived pipelines to adjust rendering behavior for shadow passes, 
    /// such as using different shaders or render states.
    /// </summary>
    public bool IsShadowPass
    {
        get => _isShadowPass;
        set => SetField(ref _isShadowPass, value);
    }

    private ViewportRenderCommandContainer _commandChain = [];
    [YamlIgnore]
    public ulong CommandGeneration { get; private set; }

    /// <summary>
    /// Gets the command chain for this pipeline.
    /// The command chain represents the sequence of render commands that will be executed by this pipeline.
    /// </summary>
    public ViewportRenderCommandContainer CommandChain
    {
        get => _commandChain;
        protected set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetField(ref _commandChain, value,
                prev =>
                {
                    if (prev is { } existing)
                        existing.ParentPipeline = null;
                },
                chain =>
                {
                    chain!.ParentPipeline = this;
                    OnCommandChainChanged();
                });
        }
    }

    /// <summary>
    /// Gets the mapping of render pass indices to their corresponding sorters.
    /// This dictionary allows the pipeline to determine the order in which render commands should be executed for each pass.
    /// </summary>
    [YamlIgnore]
    public Dictionary<int, IComparer<RenderCommand>?> PassIndicesAndSorters { get; protected set; }

    /// <summary>
    /// Gets the metadata for each render pass in this pipeline.
    /// This collection provides information about the configuration and characteristics of each render pass.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public IReadOnlyCollection<RenderPassMetadata> PassMetadata { get; private set; } = Array.Empty<RenderPassMetadata>();

    /// <summary>
    /// Initializes a new instance of the <see cref="RenderPipeline"/> class.
    /// </summary>
    /// <param name="deferCommandChainGeneration"></param>
    protected RenderPipeline(bool deferCommandChainGeneration = false)
    {
        if (!deferCommandChainGeneration)
            InitializeCommandChain();
        PassIndicesAndSorters = GetPassIndicesAndSorters();
    }

    /// <summary>
    /// Generates the command chain for this pipeline.
    /// </summary>
    /// <returns>The generated command chain.</returns>
    protected abstract ViewportRenderCommandContainer GenerateCommandChain();

    /// <summary>
    /// Gets the mapping of render pass indices to their corresponding sorters for this pipeline.
    /// </summary>
    /// <returns>The dictionary mapping render pass indices to their corresponding sorters.</returns>
    protected abstract Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters();

    /// <summary>
    /// Initializes the command chain for this pipeline.
    /// </summary>
    protected void InitializeCommandChain()
    {
        using (ViewportRenderCommandContainer.SuppressStructureChangeNotifications())
        {
            ViewportRenderCommandContainer previous = CommandChain;
            CommandChain = GenerateCommandChain();

            // Snapshot/cooked-data restoration suppresses XRBase property notifications.
            // A pipeline can still be constructed from an activation callback inside that
            // scope, so complete the ownership and derived metadata initialization that the
            // CommandChain setter normally performs.
            if (!ReferenceEquals(CommandChain.ParentPipeline, this))
            {
                previous.ParentPipeline = null;
                CommandChain.ParentPipeline = this;
                OnCommandChainChanged();
            }
        }
    }

    /// <summary>
    /// Rebuilds the command chain for this pipeline.
    /// </summary>
    protected void RebuildCommandChain()
    {
        using (ViewportRenderCommandContainer.SuppressStructureChangeNotifications())
        {
            ViewportRenderCommandContainer previous = CommandChain;
            CommandChain = GenerateCommandChain();
            if (!ReferenceEquals(CommandChain.ParentPipeline, this))
            {
                previous.ParentPipeline = null;
                CommandChain.ParentPipeline = this;
            }
        }

        NotifyCommandChainStructureChanged();
    }

    /// <summary>
    /// Allows derived pipelines to describe the resources they require via the resource layout builder.
    /// </summary>
    /// <param name="builder">The resource layout builder used to describe the resources.</param>
    protected virtual void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
    {
        // Derived pipelines can override this method to describe their required resources.
    }

    /// <summary>
    /// Determines whether this pipeline uses stereo resources for the given instance and viewport.
    /// </summary>
    /// <param name="instance">The render pipeline instance.</param>
    /// <param name="viewport">The viewport.</param>
    /// <returns>True if the pipeline uses stereo resources; otherwise, false.</returns>
    internal virtual bool UsesStereoResources(XRRenderPipelineInstance instance, XRViewport? viewport)
        => instance.RenderState.StereoPass;

    /// <summary>
    /// Builds a resource feature mask for the given render pipeline instance and viewport.
    /// </summary>
    /// <param name="instance">The render pipeline instance.</param>
    /// <param name="viewport">The viewport.</param>
    /// <returns>The resource feature mask.</returns>
    internal virtual ulong BuildResourceFeatureMaskForGenerationKey(XRRenderPipelineInstance instance, XRViewport? viewport)
        => 0UL;

    /// <summary>
    /// Handles the event when the viewport is resized. 
    /// Derived pipelines can override this method to respond to viewport size changes, 
    /// such as updating internal resources or adjusting rendering parameters.
    /// </summary>
    /// <param name="instance">The render pipeline instance.</param>
    /// <param name="width">The new width of the viewport.</param>
    /// <param name="height">The new height of the viewport.</param>
    internal virtual void HandleViewportResized(XRRenderPipelineInstance instance, int width, int height, XRViewport? viewport = null)
    {
        // Derived pipelines can override this method to handle viewport resizing events.
    }

    /// <summary>
    /// Handles the event when a texture is bound to the pipeline. 
    /// Derived pipelines can override this method to respond to texture binding events, 
    /// such as updating internal state or performing additional processing.
    /// </summary>
    /// <param name="instance">The render pipeline instance.</param>
    /// <param name="name">The name of the texture.</param>
    /// <param name="texture">The texture being bound.</param>
    /// <param name="replacedTexture">The texture being replaced, if any.</param>
    internal virtual void OnTextureBound(XRRenderPipelineInstance instance, string name, XRTexture texture, XRTexture? replacedTexture)
    {
        // Derived pipelines can override this method to handle texture binding events.
    }

    /// <summary>
    /// Handles the event when a frame buffer is bound to the pipeline.
    /// Derived pipelines can override this method to respond to frame buffer binding events, 
    /// such as updating internal state or performing additional processing.
    /// </summary>
    /// <param name="instance">The render pipeline instance.</param>
    /// <param name="name">The name of the frame buffer.</param>
    /// <param name="frameBuffer">The frame buffer being bound.</param>
    /// <param name="replacedFrameBuffer">The frame buffer being replaced, if any.</param>
    internal virtual void OnFrameBufferBound(XRRenderPipelineInstance instance, string name, XRFrameBuffer frameBuffer, XRFrameBuffer? replacedFrameBuffer)
    {
        // Derived pipelines can override this method to handle frame buffer binding events.
    }

    /// <summary>
    /// Handles the event when a texture is destroyed.
    /// Derived pipelines can override this method to respond to texture destruction events, 
    /// such as updating internal state or performing additional processing.
    /// </summary>
    /// <param name="instance">The render pipeline instance.</param>
    /// <param name="name">The name of the texture.</param>
    /// <param name="texture">The texture being destroyed.</param>
    /// <param name="reason">The reason for the texture destruction.</param>
    internal virtual void OnTextureDestroyed(XRRenderPipelineInstance instance, string name, XRTexture texture, string reason)
    {
        // Derived pipelines can override this method to handle texture destruction events.
    }

    /// <summary>
    /// Handles the event when a frame buffer is destroyed.
    /// Derived pipelines can override this method to respond to frame buffer destruction events,
    /// such as updating internal state or performing additional processing.
    /// </summary>
    /// <param name="instance">The render pipeline instance.</param>
    /// <param name="name">The name of the frame buffer.</param>
    /// <param name="frameBuffer">The frame buffer being destroyed.</param>
    /// <param name="reason">The reason for the frame buffer destruction.</param>
    internal virtual void OnFrameBufferDestroyed(XRRenderPipelineInstance instance, string name, XRFrameBuffer frameBuffer, string reason)
    {
        // Derived pipelines can override this method to handle frame buffer destruction events.
    }

    /// <summary>
    /// Builds a resource layout for this pipeline based on the provided resource profile.
    /// </summary>
    /// <param name="profile">The resource profile to use for building the layout.</param>
    /// <returns>The constructed resource layout.</returns>
    internal RenderPipelineResourceLayout BuildResourceLayout(RenderPipelineResourceProfile profile)
    {
        RenderPipelineResourceLayoutBuilder builder = new(profile);
        DescribeResources(builder);
        return builder.Build(profile);
    }

    /// <summary>
    /// Compiles a code-authored render-pipeline script into the nested VPRC command-container layout used at runtime.
    /// </summary>
    protected ViewportRenderCommandContainer CompileScript(Action<RenderPipelineScript.Builder> build)
        => RenderPipelineScript.Compile(this, build);

    /// <summary>
    /// Parses and compiles a text-authored render-pipeline script into the nested VPRC command-container layout used at runtime.
    /// </summary>
    protected ViewportRenderCommandContainer CompileScript(string script)
        => RenderPipelineScript.Compile(this, script);

    /// <summary>
    /// Allows derived pipelines to describe their render passes and associated metadata.
    /// </summary>
    /// <param name="metadata">The collection to which render pass metadata should be added.</param>
    protected virtual void DescribeRenderPasses(RenderPassMetadataCollection metadata)
    {
        // Derived pipelines can override this method to describe their render passes and associated metadata.
    }

    /// <summary>
    /// Generates the metadata for each render pass in this pipeline.
    /// </summary>
    /// <returns>A read-only collection of render pass metadata.</returns>
    protected virtual IReadOnlyCollection<RenderPassMetadata> GeneratePassMetadata()
    {
        if (CommandChain is null)
            return [];

        RenderPassMetadataCollection collection = new();
        CommandChain.BuildRenderPassMetadata(collection);
        DescribeRenderPasses(collection);
        return collection.Build();
    }

    /// <summary>
    /// Forces the pipeline to rebuild its post-processing schema so external consumers can obtain updated descriptors.
    /// </summary>
    protected void RefreshPostProcessSchema()
    {
        RenderPipelinePostProcessSchema schema;
        try
        {
            schema = BuildPostProcessSchema();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[{DebugName}] Failed to rebuild post-process schema: {ex.Message}");
            schema = RenderPipelinePostProcessSchema.Empty;
        }

        _postProcessSchema = schema ?? RenderPipelinePostProcessSchema.Empty;
    }

    /// <summary>
    /// Called when the command chain structure changes, allowing derived pipelines to respond to the change.
    /// </summary>
    protected virtual void OnCommandChainChanged()
    {
        CommandGeneration++;
        PassMetadata = GeneratePassMetadata();
        RefreshPostProcessSchema();
    }

    /// <summary>
    /// Notifies the pipeline that the command chain structure has changed, triggering updates to metadata and post-process schema.
    /// </summary>
    internal void NotifyCommandChainStructureChanged()
    {
        OnCommandChainChanged();

        for (int i = 0; i < Instances.Count; i++)
        {
            XRRenderPipelineInstance instance = Instances[i];
            instance.MeshRenderCommands.SetRenderPasses(PassIndicesAndSorters, PassMetadata);
            XRViewport? viewport = instance.LastWindowViewport;
            bool presentsDirectlyToWindow =
                viewport is not null &&
                viewport.Window?.Viewports.Contains(viewport) == true;
            bool rendersToExternalSwapchain =
                viewport?.RendersToExternalSwapchainTarget == true;

            if (presentsDirectlyToWindow || rendersToExternalSwapchain)
                instance.InvalidatePhysicalResources();
            else
                instance.DestroyCache();
        }

        if (!IsDirty)
            MarkDirty();
    }

    /// <summary>
    /// Builds the post-process schema for the pipeline.
    /// </summary>
    /// <returns>The constructed post-process schema.</returns>
    protected virtual RenderPipelinePostProcessSchema BuildPostProcessSchema()
    {
        RenderPipelinePostProcessSchemaBuilder builder = new(this);
        DescribePostProcessSchema(builder);
        return builder.Build();
    }

    /// <summary>
    /// Allows derived pipelines to describe their post-processing stages and categories via the schema builder.
    /// </summary>
    /// <param name="builder">Builder to populate with stages, uniforms, and categories.</param>
    protected virtual void DescribePostProcessSchema(RenderPipelinePostProcessSchemaBuilder builder)
    {
        // Derived pipelines can override this method to describe their post-processing stages and categories.
    }

    /// <summary>
    /// Attempts to retrieve the current render pipeline instance from the runtime rendering host services.
    /// </summary>
    protected static XRRenderPipelineInstance? TryCurrentPipeline
        => RuntimeRenderingHostServices.Current.CurrentRenderPipelineContext as XRRenderPipelineInstance
            ?? RuntimeEngine.Rendering.State.CurrentRenderingPipeline;

    /// <summary>
    /// Attempts to retrieve the current rendering state from the current render pipeline instance.
    /// </summary>
    private static RenderingState? TryState
        => TryCurrentPipeline?.RenderState;

    /// <summary>
    /// Gets the current rendering state, throwing an exception if it is not available.
    /// </summary>
    public static RenderingState State
        => TryState ?? throw new InvalidOperationException("Rendering pipeline state is not available.");

    /// <summary>
    /// Resolves the effective camera for the current frame, considering the current render pipeline instance and rendering state.
    /// </summary>
    /// <returns>The effective camera for the current frame, or null if none is available.</returns>
    private static XRCamera? ResolveEffectiveCameraForFrame()
    {
        XRRenderPipelineInstance? pipeline = TryCurrentPipeline;
        if (pipeline is not null)
        {
            return pipeline.RenderState.SceneCamera
                ?? pipeline.RenderState.RenderingCamera
                ?? pipeline.LastSceneCamera
                ?? pipeline.LastRenderingCamera;
        }

        IRuntimeRenderCommandExecutionState? renderState = RuntimeRenderingHostServices.Current.ActiveRenderCommandExecutionState;
        return renderState?.SceneCamera as XRCamera
            ?? renderState?.RenderingCamera as XRCamera;
    }

    /// <summary>
    /// Resolves the effective anti-aliasing mode for the current frame, considering the current render pipeline instance and rendering state.
    /// </summary>
    /// <returns>The effective anti-aliasing mode for the current frame.</returns>
    internal static EAntiAliasingMode ResolveEffectiveAntiAliasingModeForFrame()
    {
        if (TryCurrentPipeline?.EffectiveAntiAliasingModeThisFrame is EAntiAliasingMode latched)
            return latched;

        return ResolveEffectiveCameraForFrame()?.AntiAliasingModeOverride
            ?? RuntimeRenderingHostServices.Current.DefaultAntiAliasingMode;
    }

    /// <summary>
    /// Resolves the effective MSAA sample count for the current frame, considering the current render pipeline instance and rendering state.
    /// </summary>
    /// <returns>The effective MSAA sample count for the current frame.</returns>
    internal static uint ResolveEffectiveMsaaSampleCountForFrame() 
        => TryCurrentPipeline?.EffectiveMsaaSampleCountThisFrame is uint latched
            ? Math.Max(1u, latched)
            : Math.Max(1u,
                ResolveEffectiveCameraForFrame()?.MsaaSampleCountOverride ?? RuntimeRenderingHostServices.Current.DefaultMsaaSampleCount);

    /// <summary>
    /// Resolves the effective TSR render scale for the current frame, considering the current render pipeline instance and rendering state.
    /// </summary>
    /// <returns>The effective TSR render scale for the current frame.</returns>
    internal static float ResolveEffectiveTsrRenderScaleForFrame()
    {
        if (TryCurrentPipeline?.EffectiveTsrRenderScaleThisFrame is float latched)
            return Math.Clamp(latched, 0.5f, 1.0f);

        return Math.Clamp(
            ResolveEffectiveCameraForFrame()?.TsrRenderScaleOverride ?? RuntimeRenderingHostServices.Current.DefaultTsrRenderScale,
            0.5f,
            1.0f);
    }

    /// <summary>
    /// Attempts to retrieve a texture of the specified type and name from the current render pipeline instance.
    /// </summary>
    /// <typeparam name="T">The type of the texture to retrieve.</typeparam>
    /// <param name="name">The name of the texture to retrieve.</param>
    /// <returns>The texture if found; otherwise, null.</returns>
    public static T? GetTexture<T>(string name) where T : XRTexture
        => TryCurrentPipeline?.GetTexture<T>(name);

    /// <summary>
    /// Attempts to retrieve a texture by name from the current render pipeline instance.
    /// </summary>
    /// <param name="name">The name of the texture to retrieve.</param>
    /// <param name="texture">The retrieved texture if found; otherwise, null.</param>
    /// <returns>True if the texture was found; otherwise, false.</returns>
    public static bool TryGetTexture(string name, out XRTexture? texture)
    {
        XRRenderPipelineInstance? pipeline = TryCurrentPipeline;
        if (pipeline is null)
        {
            texture = null;
            return false;
        }

        return pipeline.TryGetTexture(name, out texture);
    }

    /// <summary>
    /// Sets a texture in the current render pipeline instance, optionally providing a descriptor for the texture resource.
    /// </summary>
    /// <param name="texture">The texture to set in the render pipeline.</param>
    /// <param name="descriptor">An optional descriptor for the texture resource.</param>
    public static void SetTexture(XRTexture texture, TextureResourceDescriptor? descriptor = null)
        => TryCurrentPipeline?.SetTexture(texture, descriptor);

    /// <summary>
    /// Attempts to retrieve a frame buffer of the specified type and name from the current render pipeline instance.
    /// </summary>
    /// <typeparam name="T">The type of the frame buffer to retrieve.</typeparam>
    /// <param name="name">The name of the frame buffer to retrieve.</param>
    /// <returns>The frame buffer if found; otherwise, null.</returns>
    public static T? GetFBO<T>(string name) where T : XRFrameBuffer
        => TryCurrentPipeline?.GetFBO<T>(name);

    /// <summary>
    /// Attempts to retrieve a frame buffer by name from the current render pipeline instance.
    /// </summary>
    /// <param name="name">The name of the frame buffer to retrieve.</param>
    /// <param name="fbo">The retrieved frame buffer if found; otherwise, null.</param>
    /// <returns>True if the frame buffer was found; otherwise, false.</returns>
    public static bool TryGetFBO(string name, out XRFrameBuffer? fbo)
    {
        XRRenderPipelineInstance? pipeline = TryCurrentPipeline;
        if (pipeline is null)
        {
            fbo = null;
            return false;
        }

        return pipeline.TryGetFBO(name, out fbo);
    }

    /// <summary>
    /// Sets a frame buffer in the current render pipeline instance, optionally providing a descriptor for the frame buffer resource.
    /// </summary>
    /// <param name="fbo">The frame buffer to set.</param>
    /// <param name="descriptor">An optional descriptor for the frame buffer resource.</param>
    public static void SetFBO(XRFrameBuffer fbo, FrameBufferResourceDescriptor? descriptor = null)
        => TryCurrentPipeline?.SetFBO(fbo, descriptor);

    // OpenGL (and most GPU APIs) disallow 0-sized textures. During startup or when a window is
    // minimized, the viewport can temporarily report 0; clamp to 1 to avoid invalid allocations.
    
    /// <summary>
    /// Gets the internal width of the current render pipeline instance or the window viewport, clamped to a minimum of 1.
    /// </summary>
    protected static uint InternalWidth
        => TryCurrentPipeline?.ResourceInternalWidth
            ?? (uint)Math.Max(1, TryState?.WindowViewport?.InternalWidth ?? 0);
    /// <summary>
    /// Gets the internal height of the current render pipeline instance or the window viewport, clamped to a minimum of 1.
    /// </summary>
    protected static uint InternalHeight
        => TryCurrentPipeline?.ResourceInternalHeight
            ?? (uint)Math.Max(1, TryState?.WindowViewport?.InternalHeight ?? 0);
    /// <summary>
    /// Gets the full width of the current render pipeline instance or the window viewport, clamped to a minimum of 1.
    /// </summary>
    protected static uint FullWidth
        => TryCurrentPipeline?.ResourceDisplayWidth
            ?? (uint)Math.Max(1, TryState?.WindowViewport?.Width ?? 0);
    /// <summary>
    /// Gets the full height of the current render pipeline instance or the window viewport, clamped to a minimum of 1.
    /// </summary>
    protected static uint FullHeight
        => TryCurrentPipeline?.ResourceDisplayHeight
            ?? (uint)Math.Max(1, TryState?.WindowViewport?.Height ?? 0);
    
    /// <summary>
    /// Checks if a texture needs to be recreated based on the internal size of the current render pipeline instance or the window viewport.
    /// </summary>
    protected static bool NeedsRecreateTextureInternalSize(XRTexture t)
    {
        uint w = InternalWidth;
        uint h = InternalHeight;
        if (w == 0 || h == 0)
        {
            string name = TryCurrentPipeline?.GetType().Name ?? "RenderPipeline";
            Debug.LogWarning($"[{name}] Internal size unavailable while checking texture resize.");
            return false;
        }
        switch (t)
        {
            case XRTexture2D t2d:
                return t2d.Width != w || t2d.Height != h;
            case XRTexture2DArray t2da:
                return t2da.Width != w || t2da.Height != h;
            case XRTexture2DView:
            case XRTexture2DArrayView:
                return false;
            default:
                // If the cached texture is the wrong type (e.g., due to stale cache/state restore),
                // keep the pipeline healthy by forcing a recreate via the factory.
                Debug.LogWarning($"Texture {t.Name} is not a 2D/2DArray texture (type={t.GetType().Name}). Forcing recreate.");
                return true;
        }
    }

    /// <summary>
    /// Checks if a texture needs to be recreated based on the full size of the current render pipeline instance or the window viewport.
    /// </summary>
    /// <param name="t">The texture to check for recreation.</param>
    /// <returns>True if the texture needs to be recreated, false otherwise.</returns>
    /// <returns></returns>
    protected static bool NeedsRecreateTextureFullSize(XRTexture t)
    {
        uint w = FullWidth;
        uint h = FullHeight;
        if (w == 0 || h == 0)
        {
            string name = TryCurrentPipeline?.GetType().Name ?? "RenderPipeline";
            Debug.LogWarning($"[{name}] Full size unavailable while checking texture resize.");
            return false;
        }
        switch (t)
        {
            case XRTexture2D t2d:
                return t2d.Width != w || t2d.Height != h;
            case XRTexture2DArray t2da:
                return t2da.Width != w || t2da.Height != h;
            case XRTexture2DView:
            case XRTexture2DArrayView:
                return false;
            default:
                Debug.LogWarning($"Texture {t.Name} is not a 2D/2DArray texture (type={t.GetType().Name}). Forcing recreate.");
                return true;
        }
    }

    /// <summary>
    /// Resizes a texture to match the internal size of the current render pipeline instance or the window viewport.
    /// </summary>
    /// <param name="t">The texture to resize.</param>
    protected static void ResizeTextureInternalSize(XRTexture t)
    {
        switch (t)
        {
            case XRTexture2D t2d:
                t2d.Resize(InternalWidth, InternalHeight);
                break;
            case XRTexture2DArray t2da:
                t2da.Resize(InternalWidth, InternalHeight);
                break;
            case XRTexture2DView:
            case XRTexture2DArrayView:
                break;
            default:
                Debug.LogWarning($"Texture {t.Name} is not a 2D or 2DArray texture. Cannot resize.");
                break;
        }
    }
    /// <summary>
    /// Resizes a texture to match the full size of the current render pipeline instance or the window viewport.
    /// </summary>
    /// <param name="t">The texture to resize.</param>
    protected static void ResizeTextureFullSize(XRTexture t)
    {
        switch (t)
        {
            case XRTexture2D t2d:
                t2d.Resize(FullWidth, FullHeight);
                break;
            case XRTexture2DArray t2da:
                t2da.Resize(FullWidth, FullHeight);
                break;
            case XRTexture2DView:
            case XRTexture2DArrayView:
                break;
            default:
                Debug.LogWarning($"Texture {t.Name} is not a 2D or 2DArray texture. Cannot resize.");
                break;
        }
    }

    /// <summary>
    /// Gets the desired frame buffer object (FBO) size based on the internal resolution of the current render pipeline instance or the window viewport.
    /// </summary>
    /// <returns>The desired FBO size as a tuple (width, height).</returns>
    protected static (uint x, uint y) GetDesiredFBOSizeInternal()
        => (InternalWidth, InternalHeight);
    /// <summary>
    /// Gets the desired frame buffer object (FBO) size based on the full resolution of the current render pipeline instance or the window viewport.
    /// </summary>
    /// <returns>The desired FBO size as a tuple (width, height).</returns>
    protected static (uint x, uint y) GetDesiredFBOSizeFull()
        => (FullWidth, FullHeight);

    /// <summary>
    /// Optional resolution request (percentage of viewport size, e.g. 0.67 = 67%) that the active
    /// pipeline instance should apply to the bound viewport before executing commands. When null,
    /// the pipeline will not override the viewport's internal resolution.
    /// </summary>
    internal float? RequestedInternalResolution { get; set; }

    /// <summary>
    /// Resolves the internal-resolution scale request for the current render. Derived pipelines can
    /// override this to make the resolution hint depend on the active camera or runtime AA mode.
    /// </summary>
    internal virtual float? GetRequestedInternalResolutionForCamera(XRCamera? camera)
        => RequestedInternalResolution;

    internal virtual float? GetRequestedInternalResolutionForCamera(XRCamera? camera, EAntiAliasingMode effectiveAntiAliasingMode)
        => GetRequestedInternalResolutionForCamera(camera);

    /// <summary>
    /// Whether managed resources for this pipeline always use the viewport display
    /// extent. Overlay/UI pipelines must not inherit a scene pipeline's reduced
    /// internal resolution when DLSS, XeSS, or TSR changes the shared viewport.
    /// </summary>
    internal virtual bool UsesDisplayResolutionForManagedResources => false;

    /// <summary>
    /// Creates a texture used by PBR shading to light an opaque surface.
    /// Input is an incoming light direction and an outgoing direction (calculated using the normal)
    /// Output from this texture is ratio of refleced radiance in the outgoing direction to irradiance from the incoming direction.
    /// https://en.wikipedia.org/wiki/Bidirectional_reflectance_distribution_function
    /// </summary>
    /// <param name="width"></param>
    /// <param name="height"></param>
    /// <returns></returns>
    public static XRTexture2D PrecomputeBRDF(uint width = 256u, uint height = 256u)
    {
        XRTexture2D brdf = new(
            width, height,
            EPixelInternalFormat.RG16f,
            EPixelFormat.Rg,
            EPixelType.HalfFloat,
            false)
        {
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            SamplerName = "BRDF",
            Name = "BRDF",
            Resizable = true,
            SizedInternalFormat = ESizedInternalFormat.Rg16f
        };

        XRShader shader = XRShader.EngineShader(Path.Combine("Scene3D", "BRDF.fs"), EShaderType.Fragment);
        XRMaterial mat = new(shader)
        {
            RenderOptions = new()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };

        XRQuadFrameBuffer fbo = new(mat);
        fbo.SetRenderTargets((brdf, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        BoundingRectangle region = new(IVector2.Zero, new IVector2((int)width, (int)height));

        //Now render the texture to the FBO using the quad
        using (fbo.BindForWritingState())
        {
            using (State.PushRenderArea(region))
            {
                //ClearColor(new ColorF4(0.0f, 0.0f, 0.0f, 1.0f));
                //Clear(true, true, false);
                fbo.Render(null, true);
            }
        }
        return brdf;
    }
}
