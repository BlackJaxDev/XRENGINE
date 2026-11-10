using System;
using System.ComponentModel;
using XREngine.Data.Rendering;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

/// <summary>
/// Retinal Visibility Cache pipeline: the VR eye pipeline for two views (stereo) or
/// four views (foveated). It executes the Advanced stage family over its view array as
/// its own frame, so its image matches the Advanced pipeline, and exposes a loud RVC
/// capability resolution. The RVC sharing stages are not scheduled until their GPU
/// kernels exist. VR selects it only through <see cref="EVrRenderPipeline.Rvc"/>.
/// </summary>
public sealed class RvcRenderPipeline : RenderPipeline, ISceneRenderPipelineFeatureProvider, IAdvancedRenderStageFamilyHost
{
    private const RenderPipelineResourceUsage RvcSampledColorAttachment =
        RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment;

    private const RenderPipelineResourceUsage RvcSampledDepthStencilAttachment =
        RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.DepthStencilAttachment;

    private const RenderPipelineResourceUsage RvcSampledStorageTexture =
        RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage;

    private const uint RvcVisibilitySourceRecordStride = 32u;
    private const uint RvcVisibilitySourceRecordCapacity = 262_144u;
    private const uint RvcMaterialResourceRowStride = 64u;
    private const uint RvcMaterialResourceRowCapacity = 65_536u;
    private const uint RvcOpenXrVisibilityMaskVertexCapacity = 65_536u;
    private const uint RvcOpenXrVisibilityMaskIndexCapacity = 131_072u;
    private const uint RvcIndirectArgumentStride = 20u;
    private const uint RvcIndirectArgumentCapacity = 65_536u;
    private const uint RvcShadeletStride = 64u;
    private const uint RvcShadeletCapacity = 524_288u;
    private const uint RvcLightClusterStride = 32u;
    private const uint RvcLightClusterCapacity = 16_384u;
    private const uint RvcLightingStride = 32u;
    private const uint RvcLightingCapacity = 524_288u;
    private const uint RvcReservoirStride = 32u;
    private const uint RvcReservoirCapacity = 131_072u;
    private const uint RvcTemporalCacheStride = 64u;
    private const uint RvcTemporalCacheCapacity = 262_144u;
    private const uint RvcCounterStride = 16u;
    private const uint RvcCounterCapacity = 256u;

    private ERvcPipelineMode _rvcPipelineMode;
    private bool _rvcQuadViewEnabled;
    private bool _rvcStereoReuseEnabled;
    private bool _rvcInsetWideReuseEnabled = true;
    private bool _rvcTemporalReuseEnabled;
    private bool _rvcPeripheralLightAggregationEnabled;
    private bool _rvcDiagnosticOverlayEnabled;
    private ERvcDebugViewMode _rvcDebugViewMode = ERvcDebugViewMode.Disabled;
    private ERvcLightGridSpace _rvcLightGridSpace = ERvcLightGridSpace.WorldAlignedCameraRelative;
    private RvcQualitySettings _rvcQualitySettings = RvcQualitySettings.Defaults;
    private RvcCapabilityMatrix _lastRvcCapabilityMatrix;
    private RvcPipelineResolution _lastRvcResolution;
    private RvcPipelinePlan _lastRvcPlan;
    private AdvancedRenderPipeline? _advancedStageFamily;
    private bool _stereo;
    private bool _rvcShadeletTelemetryEnabled;
    private bool _rvcFoveatedQuadShadingEnabled;

    /// <summary>True when this pipeline renders both eyes as one layered stereo target.</summary>
    public bool Stereo => _stereo;

    AdvancedRenderPipeline IAdvancedRenderStageFamilyHost.AdvancedStageFamilyDefinition => AdvancedStageFamily;

    /// <summary>
    /// The Advanced stage family that this pipeline executes as its own frame. It
    /// renders one mono eye, or both eyes as one layered target when this pipeline
    /// is stereo.
    /// </summary>
    internal AdvancedRenderPipeline AdvancedStageFamily
        => _advancedStageFamily is { } family && family.Stereo == Stereo
            ? family
            // The base constructor queries passes before Stereo is assigned, so a
            // family made for the other topology is replaced, never reused.
            : _advancedStageFamily = CreateAdvancedStageFamily();

    /// <summary>Gets the existing stage family when it matches this pipeline's view topology.</summary>
    public AdvancedRenderPipeline? GetExistingAdvancedStageFamily()
    {
        AdvancedRenderPipeline? family = System.Threading.Volatile.Read(ref _advancedStageFamily);
        return family?.Stereo == Stereo ? family : null;
    }

    // The family owns the scene feature values; this pipeline's feature properties
    // forward to it, so camera-source feature copies reach it directly.
    private AdvancedRenderPipeline CreateAdvancedStageFamily()
    {
        AdvancedRenderPipeline family = AdvancedRenderPipeline.CreateOpenXrEyeStageFamilyDefinition(Stereo);
        // The telemetry counters are small; declaring them for every RVC family
        // lets the setting toggle per frame without a resource rebuild.
        family.DeclaresRvcShadeletTelemetry = true;
        return family;
    }

    /// <summary>
    /// True when the native opaque shading stage also runs the RVC shadelet telemetry
    /// kernel. The kernel only measures reuse ceilings; shading output does not change.
    /// </summary>
    [Category("RVC")]
    [DisplayName("Shadelet Telemetry")]
    [Description("Runs the RVC shadelet telemetry kernel and reads its counters back. Shading output does not change.")]
    public bool RvcShadeletTelemetryEnabled
    {
        get => _rvcShadeletTelemetryEnabled;
        set => SetField(ref _rvcShadeletTelemetryEnabled, value);
    }

    /// <summary>
    /// True when native opaque shading shades one pixel per peripheral 2x2 or 4x4 block
    /// of the same primitive and a fill pass copies it to the rest of the block.
    /// </summary>
    [Category("RVC")]
    [DisplayName("Foveated Quad Shading")]
    [Description("Shades one pixel per peripheral 2x2 or 4x4 block of the same triangle and copies it to the rest of the block. Vulkan eyes without MSAA only.")]
    public bool RvcFoveatedQuadShadingEnabled
    {
        get => _rvcFoveatedQuadShadingEnabled;
        set => SetField(ref _rvcFoveatedQuadShadingEnabled, value);
    }

    public override IRenderPipelineEditorUIProvider? EditorUIProvider
        => AdvancedStageFamily.EditorUIProvider;

    public override IReadOnlyList<string> PreferredPreviewTextureNames
        => AdvancedStageFamily.PreferredPreviewTextureNames;

    public override IReadOnlyList<string> PreferredPreviewFrameBufferNames
        => AdvancedStageFamily.PreferredPreviewFrameBufferNames;

    public override void DescribeRequirements(RenderPipelineRequirements requirements)
        => AdvancedStageFamily.DescribeRequirements(requirements);

    internal override bool RequiresCanonicalGpuScenePublication => true;

    internal override float? GetRequestedInternalResolutionForCamera(XRCamera? camera)
        => AdvancedStageFamily.GetRequestedInternalResolutionForCamera(camera);

    internal override float? GetRequestedInternalResolutionForCamera(XRCamera? camera, EAntiAliasingMode effectiveAntiAliasingMode)
        => AdvancedStageFamily.GetRequestedInternalResolutionForCamera(camera, effectiveAntiAliasingMode);

    internal override RenderPipelineResourceVariant BuildResourceVariantForGenerationKey(XRRenderPipelineInstance instance, XRViewport? viewport)
        => AdvancedStageFamily.BuildResourceVariantForGenerationKey(instance, viewport);

    internal override bool UsesStereoResources(XRRenderPipelineInstance instance, XRViewport? viewport)
        => AdvancedStageFamily.UsesStereoResources(instance, viewport);

    internal override void HandleViewportResized(XRRenderPipelineInstance instance, int width, int height, XRViewport? viewport = null)
        => AdvancedStageFamily.HandleViewportResized(instance, width, height, viewport);

    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => AdvancedStageFamily.GetHostedPassIndicesAndSorters();

    protected override void DescribePostProcessSchema(RenderPipelinePostProcessSchemaBuilder builder)
        => AdvancedStageFamily.DescribeHostedPostProcessSchema(builder);

    protected override Lazy<XRMaterial> InvalidMaterialFactory
        => new(XRMaterial.CreateColorMaterialDeferred, LazyThreadSafetyMode.PublicationOnly);

    // Scene feature settings are owned by the composed family, so the camera-source
    // synchronizer and the GI and lighting passes see one set of values.
    public bool ForwardDepthPrePassEnabled
    {
        get => AdvancedStageFamily.ForwardDepthPrePassEnabled;
        set => AdvancedStageFamily.ForwardDepthPrePassEnabled = value;
    }

    public bool ForwardPrePassSharesGBufferTargets
    {
        get => AdvancedStageFamily.ForwardPrePassSharesGBufferTargets;
        set => AdvancedStageFamily.ForwardPrePassSharesGBufferTargets = value;
    }

    public EDepthNormalPrePassResolution ForwardDepthNormalPrePassResolution
    {
        get => AdvancedStageFamily.ForwardDepthNormalPrePassResolution;
        set => AdvancedStageFamily.ForwardDepthNormalPrePassResolution = value;
    }

    public EGlobalIlluminationMode GlobalIlluminationMode
    {
        get => AdvancedStageFamily.GlobalIlluminationMode;
        set => AdvancedStageFamily.GlobalIlluminationMode = value;
    }

    public GlobalIlluminationPlan GlobalIlluminationPlan => AdvancedStageFamily.GlobalIlluminationPlan;

    public XRTexture2DArray? ProbeIrradianceArray => AdvancedStageFamily.ProbeIrradianceArray;

    public XRTexture2DArray? ProbePrefilterArray => AdvancedStageFamily.ProbePrefilterArray;

    public int ProbeCount => AdvancedStageFamily.ProbeCount;

    public bool BindPbrLightingResources(XRRenderProgram program, bool deferredProbeBufferBindings = false)
        => AdvancedStageFamily.BindPbrLightingResources(program, deferredProbeBufferBindings);

    public void SyncPbrLightingResourcesForFrame()
        => AdvancedStageFamily.SyncPbrLightingResourcesForFrame();

    public XRMaterial GetMotionVectorsMaterial()
        => AdvancedStageFamily.GetMotionVectorsMaterial();

    public XRMaterial GetDepthNormalPrePassMaterial()
        => AdvancedStageFamily.GetDepthNormalPrePassMaterial();

    public XRMaterial GetFullOverdrawCountMaterial()
        => AdvancedStageFamily.GetFullOverdrawCountMaterial();

    public RvcRenderPipeline() : this(stereo: false)
    {
    }

    public RvcRenderPipeline(bool stereo = false, ERvcPipelineMode mode = ERvcPipelineMode.ForwardPlusOracle)
        : base(deferCommandChainGeneration: true)
    {
        _stereo = stereo;
        _rvcPipelineMode = mode;
        RefreshRvcResolution();
        InitializeCommandChain();
    }

    protected override void OnDestroying()
    {
        base.OnDestroying();
        _advancedStageFamily?.Destroy();
        _advancedStageFamily = null;
    }

    public override string DebugName => "RvcRenderPipeline";

    [Category("RVC")]
    [DisplayName("Pipeline Mode")]
    [Description("Requested Retinal Visibility Cache mode. Unsupported modes fall back visibly to the Forward+ oracle path.")]
    public ERvcPipelineMode RvcPipelineMode
    {
        get => _rvcPipelineMode;
        set
        {
            if (SetField(ref _rvcPipelineMode, value))
                RefreshRvcResolution();
        }
    }

    [Category("RVC")]
    [DisplayName("Quad View")]
    [Description("Requests RVC quad-view rendering. Requires a runtime quad-view capability and otherwise reports MissingQuadViewRuntime.")]
    public bool RvcQuadViewEnabled
    {
        get => _rvcQuadViewEnabled;
        set
        {
            if (SetField(ref _rvcQuadViewEnabled, value))
                RefreshRvcResolution();
        }
    }

    [Category("RVC")]
    [DisplayName("Stereo Reuse")]
    [Description("Allows shadelet reuse between stereo views after validation. Defaults off until the A/B harness is green.")]
    public bool RvcStereoReuseEnabled
    {
        get => _rvcStereoReuseEnabled;
        set
        {
            if (SetField(ref _rvcStereoReuseEnabled, value))
                RefreshRvcResolution();
        }
    }

    [Category("RVC")]
    [DisplayName("Inset/Wide Reuse")]
    [Description("Allows the RVC inset view to reuse validated wide-view shadelets when identity, depth, normal, and material generations match.")]
    public bool RvcInsetWideReuseEnabled
    {
        get => _rvcInsetWideReuseEnabled;
        set
        {
            if (SetField(ref _rvcInsetWideReuseEnabled, value))
                RefreshRvcResolution();
        }
    }

    [Category("RVC")]
    [DisplayName("Temporal Reuse")]
    [Description("Allows temporal shadelet reuse after validation. Defaults off for deterministic oracle comparison.")]
    public bool RvcTemporalReuseEnabled
    {
        get => _rvcTemporalReuseEnabled;
        set
        {
            if (SetField(ref _rvcTemporalReuseEnabled, value))
                RefreshRvcResolution();
        }
    }

    [Category("RVC")]
    [DisplayName("Peripheral Light Aggregation")]
    [Description("Allows periphery-only light aggregation through the head-space light grid when cache passes are active.")]
    public bool RvcPeripheralLightAggregationEnabled
    {
        get => _rvcPeripheralLightAggregationEnabled;
        set
        {
            if (SetField(ref _rvcPeripheralLightAggregationEnabled, value))
                RefreshRvcResolution();
        }
    }

    [Category("RVC")]
    [DisplayName("Diagnostic Overlay")]
    [Description("Enables RVC diagnostic overlay publishing when a debug view mode is selected.")]
    public bool RvcDiagnosticOverlayEnabled
    {
        get => _rvcDiagnosticOverlayEnabled;
        set
        {
            if (SetField(ref _rvcDiagnosticOverlayEnabled, value))
                RefreshRvcResolution();
        }
    }

    [Category("RVC")]
    [DisplayName("Debug View")]
    [Description("RVC diagnostic view published to the mirror/debug output.")]
    public ERvcDebugViewMode RvcDebugViewMode
    {
        get => _rvcDebugViewMode;
        set
        {
            if (SetField(ref _rvcDebugViewMode, value))
                RefreshRvcResolution();
        }
    }

    [Category("RVC")]
    [DisplayName("Light Grid Space")]
    [Description("Coordinate policy for RVC shared-lighting clusters. World-aligned camera-relative is the deterministic default.")]
    public ERvcLightGridSpace RvcLightGridSpace
    {
        get => _rvcLightGridSpace;
        set
        {
            if (SetField(ref _rvcLightGridSpace, value))
                RefreshRvcResolution();
        }
    }

    [Browsable(false)]
    public RvcRenderingSettings RvcSettings
    {
        get => BuildSettingsSnapshot();
        set => ApplyRvcSettings(value);
    }

    [Browsable(false)]
    public RvcQualitySettings RvcQualitySettings
    {
        get => _rvcQualitySettings;
        set
        {
            if (SetField(ref _rvcQualitySettings, value))
                RefreshRvcResolution();
        }
    }

    [Browsable(false)]
    public RvcCapabilityMatrix LastRvcCapabilityMatrix
    {
        get => _lastRvcCapabilityMatrix;
        private set => SetField(ref _lastRvcCapabilityMatrix, value);
    }

    [Browsable(false)]
    public RvcPipelineResolution LastRvcResolution
    {
        get => _lastRvcResolution;
        private set => SetField(ref _lastRvcResolution, value);
    }

    [Browsable(false)]
    public RvcPipelinePlan LastRvcPlan
    {
        get => _lastRvcPlan;
        private set => SetField(ref _lastRvcPlan, value);
    }

    [Browsable(false)]
    public ERvcFallbackReason LastRvcFallbackReason => LastRvcResolution.FallbackReason;

    [Browsable(false)]
    public bool UsesRvcCachePasses => LastRvcResolution.IsRvcActive;

    public void ApplyRvcSettings(RvcRenderingSettings settings)
    {
        bool changed = false;
        changed |= SetField(ref _rvcPipelineMode, settings.PipelineMode, nameof(RvcPipelineMode));
        changed |= SetField(ref _rvcQuadViewEnabled, settings.QuadViewEnabled, nameof(RvcQuadViewEnabled));
        changed |= SetField(ref _rvcStereoReuseEnabled, settings.StereoReuseEnabled, nameof(RvcStereoReuseEnabled));
        changed |= SetField(ref _rvcInsetWideReuseEnabled, settings.InsetWideReuseEnabled, nameof(RvcInsetWideReuseEnabled));
        changed |= SetField(ref _rvcTemporalReuseEnabled, settings.TemporalReuseEnabled, nameof(RvcTemporalReuseEnabled));
        changed |= SetField(ref _rvcPeripheralLightAggregationEnabled, settings.PeripheralLightAggregationEnabled, nameof(RvcPeripheralLightAggregationEnabled));
        changed |= SetField(ref _rvcDiagnosticOverlayEnabled, settings.DiagnosticOverlayEnabled, nameof(RvcDiagnosticOverlayEnabled));
        changed |= SetField(ref _rvcDebugViewMode, settings.DebugViewMode, nameof(RvcDebugViewMode));
        changed |= SetField(ref _rvcLightGridSpace, settings.LightGridSpace, nameof(RvcLightGridSpace));

        if (changed)
            RefreshRvcResolution();
    }

    /// <summary>
    /// Applies the engine-owned RVC mode and quality settings to this output-local pipeline.
    /// </summary>
    public void ApplyRuntimeSettings(
        RuntimeEngine.Rendering.EngineSettings settings,
        ERvcPipelineMode? pipelineModeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ApplyRvcSettings(new RvcRenderingSettings(
            pipelineModeOverride ?? settings.RvcPipelineMode,
            settings.RvcQuadViewEnabled,
            settings.RvcStereoReuseEnabled,
            settings.RvcInsetWideReuseEnabled,
            settings.RvcTemporalReuseEnabled,
            settings.RvcPeripheralLightAggregationEnabled,
            settings.RvcDiagnosticOverlayEnabled,
            settings.RvcDebugViewMode,
            settings.RvcLightGridSpace));

        RvcShadeletTelemetryEnabled = settings.RvcShadeletTelemetryEnabled;
        RvcFoveatedQuadShadingEnabled = settings.RvcFoveatedQuadShadingEnabled;
        RvcQualitySettings = new RvcQualitySettings(
            settings.RvcFovealRadiusDegrees,
            settings.RvcGuardBandDegrees,
            settings.RvcMidFieldRadiusDegrees,
            settings.RvcPeripheralMaxRate,
            settings.RvcForceFullResNearDistanceMeters,
            settings.RvcDerivativeStrategy,
            settings.RvcFovealAntiAliasingPath,
            settings.RvcReuseMaxNormalAngleDegrees,
            settings.RvcReuseMaxDepthDeltaMeters,
            settings.RvcReuseMaxRoughnessBucketDelta);
    }

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        // The OpenXR eye profile of the Advanced family acquires the shared
        // preparation itself. RVC stages are not appended yet: their kernels do
        // not exist, and the family must match the Advanced oracle first.
        return AdvancedStageFamily.CreateHostedCommandChain(this);
    }

    public RvcPipelineResolution RefreshRvcResolution()
    {
        RvcRenderingSettings settings = BuildSettingsSnapshot();
        RvcCapabilityMatrix capabilities = BuildRuntimeCapabilityMatrix(settings);
        RvcPipelineResolution resolution = RvcPipelineResolver.Resolve(settings, capabilities);
        RvcPipelinePlan plan = RvcPipelinePlan.Build(settings, RvcQualitySettings, resolution, capabilities);

        LastRvcCapabilityMatrix = capabilities;
        LastRvcResolution = resolution;
        LastRvcPlan = plan;
        ReportFallbackIfNeeded(resolution);
        return resolution;
    }

    protected override void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
    {
        // RVC stage resources are declared only with their stages; see GenerateCommandChain.
        AdvancedStageFamily.DescribeHostedResources(builder);
    }

    /// <summary>
    /// RVC targets and buffers exist only while the resolved plan schedules RVC GPU
    /// stages. <see cref="ERvcPipelineMode.Off"/> and the Forward+ oracle plan none,
    /// and declaring the set anyway allocated about 0.8 GB of two-layer stereo
    /// targets at 2688² per eye that no pass writes.
    /// </summary>
    internal bool RequiresRvcResources
        => LastRvcPlan.GpuPassExecution.PlannedStages != ERvcGpuPassStage.None;

    internal override ulong BuildResourceFeatureMaskForGenerationKey(
        XRRenderPipelineInstance instance,
        XRViewport? viewport)
        => AdvancedStageFamily.BuildResourceFeatureMaskForGenerationKey(instance, viewport);

    private static void AppendRvcPassCommands(ViewportRenderCommandContainer commands)
    {
        AddRvcPass(commands, ERvcGpuPassStage.OpenXrVisibilityMaskStencil);
        AddRvcPass(commands, ERvcGpuPassStage.VisibilityTargets);
        AddRvcPass(commands, ERvcGpuPassStage.AttributeReconstruction);
        AddRvcPass(commands, ERvcGpuPassStage.HzbRejection);
        AddRvcPass(commands, ERvcGpuPassStage.PixelToShadeletMap);
        AddRvcPass(commands, ERvcGpuPassStage.MaterialShadeletShading);
        AddRvcPass(commands, ERvcGpuPassStage.FoveatedShadingRate);
        AddRvcPass(commands, ERvcGpuPassStage.HeadSpaceLightClusters);
        AddRvcPass(commands, ERvcGpuPassStage.SharedLighting);
        AddRvcPass(commands, ERvcGpuPassStage.ReuseValidation);
        AddRvcPass(commands, ERvcGpuPassStage.TemporalCache);
        AddRvcPass(commands, ERvcGpuPassStage.TransparencyForwardPlus);
        AddRvcPass(commands, ERvcGpuPassStage.FoveatedResolve);
        AddRvcPass(commands, ERvcGpuPassStage.DiagnosticOverlay);
    }

    private static void AddRvcPass(ViewportRenderCommandContainer commands, ERvcGpuPassStage stage)
    {
        VPRC_RvcPass pass = commands.Add<VPRC_RvcPass>();
        pass.Stage = stage;
    }

    private void DeclareRvcResources(RenderPipelineResourceLayoutBuilder builder)
    {
        uint layerCount = ResolveRvcViewLayerCount(builder.Profile);
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();

        Texture(builder, RvcFrameGraphContract.PerViewDepthArray, internalSize, RvcSampledDepthStencilAttachment,
            EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, ESizedInternalFormat.Depth24Stencil8,
            EFrameBufferAttachment.DepthStencilAttachment, storage: false)
            .Layers(layerCount)
            .StereoCompatible(layerCount > 1u)
            .DebugLabel("RVC per-view depth/stencil")
            .Add();

        Texture(builder, RvcFrameGraphContract.PerViewVisibilityArray, internalSize, RvcSampledColorAttachment | RenderPipelineResourceUsage.StorageImage,
            EPixelInternalFormat.RG32ui, EPixelFormat.RgInteger, EPixelType.UnsignedInt, ESizedInternalFormat.Rg32ui,
            EFrameBufferAttachment.ColorAttachment0, storage: true)
            .Layers(layerCount)
            .StereoCompatible(layerCount > 1u)
            .DependsOn(RvcFrameGraphContract.PerViewDepthArray)
            .DebugLabel("RVC visibility identity")
            .Add();

        Texture(builder, RvcFrameGraphContract.PerViewVelocityArray, internalSize, RvcSampledColorAttachment,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            EFrameBufferAttachment.ColorAttachment1, storage: false)
            .Layers(layerCount)
            .StereoCompatible(layerCount > 1u)
            .DependsOn(RvcFrameGraphContract.PerViewVisibilityArray)
            .DebugLabel("RVC velocity")
            .Add();

        Texture(builder, RvcFrameGraphContract.PerViewHzbDepthArray, internalSize, RvcSampledStorageTexture,
            EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f,
            null, storage: true, mipLevelCount: 8u)
            .Layers(layerCount)
            .StereoCompatible(layerCount > 1u)
            .DependsOn(RvcFrameGraphContract.PerViewDepthArray)
            .DebugLabel("RVC conservative HZB")
            .Add();

        Texture(builder, RvcFrameGraphContract.PerViewReconstructionErrorArray, internalSize, RvcSampledStorageTexture,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            null, storage: true)
            .Layers(layerCount)
            .StereoCompatible(layerCount > 1u)
            .DependsOn(RvcFrameGraphContract.PerViewVisibilityArray)
            .DebugLabel("RVC reconstruction error")
            .Add();

        Texture(builder, RvcFrameGraphContract.PerViewPixelToShadeletArray, internalSize, RvcSampledStorageTexture,
            EPixelInternalFormat.R32ui, EPixelFormat.RedInteger, EPixelType.UnsignedInt, ESizedInternalFormat.R32ui,
            null, storage: true)
            .Layers(layerCount)
            .StereoCompatible(layerCount > 1u)
            .DependsOn(RvcFrameGraphContract.PerViewReconstructionErrorArray)
            .DebugLabel("RVC pixel-to-shadelet map")
            .Add();

        Texture(builder, RvcFrameGraphContract.TransparencyTargetArray, internalSize, RvcSampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            EFrameBufferAttachment.ColorAttachment0, storage: false)
            .Layers(layerCount)
            .StereoCompatible(layerCount > 1u)
            .DependsOn(RvcFrameGraphContract.SharedLighting)
            .DebugLabel("RVC transparent Forward+ fallback")
            .Add();

        Texture(builder, RvcFrameGraphContract.FinalResolveArray, internalSize, RvcSampledColorAttachment | RenderPipelineResourceUsage.TransferSource,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            EFrameBufferAttachment.ColorAttachment0, storage: false)
            .Layers(layerCount)
            .StereoCompatible(layerCount > 1u)
            .DependsOn(RvcFrameGraphContract.TransparencyTargetArray)
            .DebugLabel("RVC final resolve")
            .Add();

        Texture(builder, RvcFrameGraphContract.MirrorDebug, internalSize, RvcSampledColorAttachment | RenderPipelineResourceUsage.TransferSource,
            EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, ESizedInternalFormat.Rgba8,
            EFrameBufferAttachment.ColorAttachment0, storage: false)
            .Layers(layerCount)
            .StereoCompatible(layerCount > 1u)
            .DebugLabel("RVC mirror/debug output")
            .Add();

        Buffer(builder, RvcFrameGraphContract.SharedVisibilitySourceRecords, RvcVisibilitySourceRecordStride, RvcVisibilitySourceRecordCapacity, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .DebugLabel("RVC visibility source records")
            .Add();
        Buffer(builder, RvcFrameGraphContract.SharedMaterialResourceRows, RvcMaterialResourceRowStride, RvcMaterialResourceRowCapacity, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .DebugLabel("RVC renderer material resource rows")
            .Add();
        Buffer(builder, RvcFrameGraphContract.SharedOpenXrVisibilityMaskVertices, sizeof(float) * 2u, RvcOpenXrVisibilityMaskVertexCapacity, EBufferTarget.ArrayBuffer, EBufferUsage.DynamicDraw)
            .DebugLabel("RVC XR visibility mask vertices")
            .Add();
        Buffer(builder, RvcFrameGraphContract.SharedOpenXrVisibilityMaskIndices, sizeof(uint), RvcOpenXrVisibilityMaskIndexCapacity, EBufferTarget.ElementArrayBuffer, EBufferUsage.DynamicDraw)
            .DebugLabel("RVC XR visibility mask indices")
            .Add();
        Buffer(builder, RvcFrameGraphContract.SharedIndirectArguments, RvcIndirectArgumentStride, RvcIndirectArgumentCapacity, EBufferTarget.DrawIndirectBuffer, EBufferUsage.DynamicDraw)
            .DebugLabel("RVC GPU indirect arguments")
            .Add();
        Buffer(builder, RvcFrameGraphContract.SharedMaterialShadelets, RvcShadeletStride, RvcShadeletCapacity, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .DebugLabel("RVC material shadelets")
            .Add();
        Buffer(builder, RvcFrameGraphContract.SharedHeadSpaceLightClusters, RvcLightClusterStride, RvcLightClusterCapacity, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .DebugLabel("RVC head-space light clusters")
            .Add();
        Buffer(builder, RvcFrameGraphContract.SharedLighting, RvcLightingStride, RvcLightingCapacity, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .DebugLabel("RVC shared lighting")
            .Add();
        Buffer(builder, RvcFrameGraphContract.SharedLightReservoirs, RvcReservoirStride, RvcReservoirCapacity, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .DebugLabel("RVC peripheral light reservoirs")
            .Add();
        Buffer(builder, RvcFrameGraphContract.SharedTemporalCache, RvcTemporalCacheStride, RvcTemporalCacheCapacity, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .DebugLabel("RVC temporal cache")
            .Add();
        Buffer(builder, RvcFrameGraphContract.SharedCounters, RvcCounterStride, RvcCounterCapacity, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicRead)
            .DebugLabel("RVC delayed counters")
            .Add();

        builder.FrameBuffer(RvcFrameGraphContract.VisibilityFrameBuffer)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(internalSize)
            .Color(0, RvcFrameGraphContract.PerViewVisibilityArray, layerIndex: -1)
            .Color(1, RvcFrameGraphContract.PerViewVelocityArray, layerIndex: -1)
            .DepthStencil(RvcFrameGraphContract.PerViewDepthArray, layerIndex: -1)
            .Factory(CreateRvcVisibilityFrameBuffer)
            .DebugLabel("RVC visibility FBO")
            .Add();

        builder.FrameBuffer(RvcFrameGraphContract.TransparencyFrameBuffer)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(internalSize)
            .Color(0, RvcFrameGraphContract.TransparencyTargetArray, layerIndex: -1)
            .DepthStencil(RvcFrameGraphContract.PerViewDepthArray, layerIndex: -1)
            .Factory(CreateRvcTransparencyFrameBuffer)
            .DebugLabel("RVC transparency FBO")
            .Add();

        builder.FrameBuffer(RvcFrameGraphContract.ResolveFrameBuffer)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(internalSize)
            .Color(0, RvcFrameGraphContract.FinalResolveArray, layerIndex: -1)
            .Factory(CreateRvcResolveFrameBuffer)
            .DebugLabel("RVC resolve FBO")
            .Add();

        builder.FrameBuffer(RvcFrameGraphContract.DebugFrameBuffer)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(internalSize)
            .Color(0, RvcFrameGraphContract.MirrorDebug)
            .Factory(CreateRvcDebugFrameBuffer)
            .DebugLabel("RVC debug FBO")
            .Add();
    }

    private RenderPipelineResourceLayoutBuilder.TextureSpecBuilder Texture(
        RenderPipelineResourceLayoutBuilder builder,
        string name,
        RenderResourceSizePolicy sizePolicy,
        RenderPipelineResourceUsage usage,
        EPixelInternalFormat internalFormat,
        EPixelFormat pixelFormat,
        EPixelType pixelType,
        ESizedInternalFormat sizedInternalFormat,
        EFrameBufferAttachment? attachment,
        bool storage,
        uint mipLevelCount = 1u)
    {
        uint layerCount = ResolveRvcViewLayerCount(builder.Profile);
        mipLevelCount = Math.Max(1u, mipLevelCount);
        RenderPipelineResourceLayoutBuilder.TextureSpecBuilder texture =
            builder.Texture(name)
            .Size(sizePolicy)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(usage)
            .Format(internalFormat, pixelFormat, pixelType)
            .SizedFormat(sizedInternalFormat)
            .RequiresStorageUsage(storage)
            .Factory(() => CreateRvcTexture(
                name,
                internalFormat,
                pixelFormat,
                pixelType,
                sizedInternalFormat,
                attachment,
                storage,
                layerCount,
                mipLevelCount));

        if (mipLevelCount > 1u)
        {
            texture.Mips(new RenderResourceMipPolicy(
                0u,
                mipLevelCount,
                AutoGenerateMipmaps: false,
                RequireImmutableStorage: true));
        }

        return texture;
    }

    private static uint ResolveRvcViewLayerCount(
        in RenderPipelineResourceProfile profile)
        => Math.Max(profile.ViewCount, profile.Stereo ? 2u : 1u);

    private static RenderPipelineResourceLayoutBuilder.BufferSpecBuilder Buffer(
        RenderPipelineResourceLayoutBuilder builder,
        string name,
        uint strideBytes,
        uint elementCount,
        EBufferTarget target,
        EBufferUsage usage)
        => builder.Buffer(name)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(target switch
            {
                EBufferTarget.DrawIndirectBuffer or EBufferTarget.DispatchIndirectBuffer => RenderPipelineResourceUsage.IndirectBuffer | RenderPipelineResourceUsage.StorageBuffer,
                EBufferTarget.ElementArrayBuffer => RenderPipelineResourceUsage.IndexBuffer,
                EBufferTarget.ArrayBuffer => RenderPipelineResourceUsage.VertexBuffer | RenderPipelineResourceUsage.StorageBuffer,
                _ => RenderPipelineResourceUsage.StorageBuffer,
            })
            .BufferFormat((ulong)strideBytes * elementCount, target, usage)
            .Elements(strideBytes, elementCount)
            .Access(usage == EBufferUsage.DynamicRead ? EBufferAccessPattern.ReadOnly : EBufferAccessPattern.ReadWrite)
            .Factory(() => CreateRvcBuffer(name, strideBytes, elementCount, target, usage));

    private XRTexture CreateRvcTexture(
        string name,
        EPixelInternalFormat internalFormat,
        EPixelFormat pixelFormat,
        EPixelType pixelType,
        ESizedInternalFormat sizedInternalFormat,
        EFrameBufferAttachment? attachment,
        bool storage,
        uint layerCount,
        uint mipLevelCount)
    {
        XRTexture2DArray texture = attachment.HasValue
            ? XRTexture2DArray.CreateFrameBufferTexture(
                layerCount,
                InternalWidth,
                InternalHeight,
                internalFormat,
                pixelFormat,
                pixelType,
                attachment.Value)
            : XRTexture2DArray.CreateFrameBufferTexture(
                layerCount,
                InternalWidth,
                InternalHeight,
                internalFormat,
                pixelFormat,
                pixelType);

        if (mipLevelCount > 1u)
            ConfigureRvcMipChain(texture, mipLevelCount, internalFormat, pixelFormat, pixelType);

        if (layerCount > 1u)
            texture.OVRMultiViewParameters = new(0, layerCount);

        texture.Resizable = false;
        texture.SizedInternalFormat = sizedInternalFormat;
        texture.RequiresStorageUsage = storage;
        texture.Name = name;
        texture.SamplerName = name;
        return texture;
    }

    private static void ConfigureRvcMipChain(
        XRTexture2DArray texture,
        uint mipLevelCount,
        EPixelInternalFormat internalFormat,
        EPixelFormat pixelFormat,
        EPixelType pixelType)
    {
        int count = checked((int)mipLevelCount);
        foreach (XRTexture2D layer in texture.Textures)
        {
            Mipmap2D[] mipmaps = new Mipmap2D[count];
            uint width = layer.Width;
            uint height = layer.Height;
            for (int mipIndex = 0; mipIndex < count; mipIndex++)
            {
                mipmaps[mipIndex] = new Mipmap2D(
                    width,
                    height,
                    internalFormat,
                    pixelFormat,
                    pixelType,
                    allocateData: false);
                width = Math.Max(1u, width >> 1);
                height = Math.Max(1u, height >> 1);
            }

            layer.Mipmaps = mipmaps;
            layer.AutoGenerateMipmaps = false;
        }
    }

    private static XRDataBuffer CreateRvcBuffer(
        string name,
        uint strideBytes,
        uint elementCount,
        EBufferTarget target,
        EBufferUsage usage)
    {
        XRDataBuffer buffer = new(
            name,
            target,
            Math.Max(1u, elementCount),
            EComponentType.Struct,
            Math.Max(1u, strideBytes),
            normalize: false,
            integral: true)
        {
            Usage = usage,
        };
        return buffer;
    }

    private XRFrameBuffer CreateRvcVisibilityFrameBuffer()
        => new(
            (RequireRvcAttachment(RvcFrameGraphContract.PerViewVisibilityArray), EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (RequireRvcAttachment(RvcFrameGraphContract.PerViewVelocityArray), EFrameBufferAttachment.ColorAttachment1, 0, -1),
            (RequireRvcAttachment(RvcFrameGraphContract.PerViewDepthArray), EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = RvcFrameGraphContract.VisibilityFrameBuffer,
        };

    private XRFrameBuffer CreateRvcTransparencyFrameBuffer()
        => new(
            (RequireRvcAttachment(RvcFrameGraphContract.TransparencyTargetArray), EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (RequireRvcAttachment(RvcFrameGraphContract.PerViewDepthArray), EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = RvcFrameGraphContract.TransparencyFrameBuffer,
        };

    private XRFrameBuffer CreateRvcResolveFrameBuffer()
        => new((RequireRvcAttachment(RvcFrameGraphContract.FinalResolveArray), EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = RvcFrameGraphContract.ResolveFrameBuffer,
        };

    private XRFrameBuffer CreateRvcDebugFrameBuffer()
        => new((RequireRvcAttachment(RvcFrameGraphContract.MirrorDebug), EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = RvcFrameGraphContract.DebugFrameBuffer,
        };

    private static IFrameBufferAttachement RequireRvcAttachment(string textureName)
    {
        XRTexture? texture = GetTexture<XRTexture>(textureName);
        if (texture is IFrameBufferAttachement attachment)
            return attachment;

        throw new InvalidOperationException($"RVC framebuffer attachment '{textureName}' is missing or is not framebuffer-attachable.");
    }

    private RvcRenderingSettings BuildSettingsSnapshot()
        => new(
            _rvcPipelineMode,
            _rvcQuadViewEnabled,
            _rvcStereoReuseEnabled,
            _rvcInsetWideReuseEnabled,
            _rvcTemporalReuseEnabled,
            _rvcPeripheralLightAggregationEnabled,
            _rvcDiagnosticOverlayEnabled,
            _rvcDebugViewMode,
            _rvcLightGridSpace);

    private static RvcCapabilityMatrix BuildRuntimeCapabilityMatrix(in RvcRenderingSettings settings)
    {
        IRuntimeRenderFrameTimingServices frameTiming = RuntimeRenderingHostServices.FrameTiming;
        IRuntimeRenderSettingsServices settingsServices = RuntimeRenderingHostServices.Settings;
        IRuntimeRenderPresentationServices presentation = RuntimeRenderingHostServices.Presentation;
        RuntimeGraphicsApiKind backend = frameTiming.CurrentRenderBackend;
        IRuntimeRendererHost? renderer = frameTiming.CurrentRenderer;
        bool vulkan = backend == RuntimeGraphicsApiKind.Vulkan;
        bool openGl = backend == RuntimeGraphicsApiKind.OpenGL;
        ERvcDescriptorBackend rendererDescriptorBackend = renderer?.RvcDescriptorBackend ?? ERvcDescriptorBackend.None;
        ERvcVulkanProductionFeature rendererVulkanFeatures = renderer?.RvcVulkanProductionFeatures ?? ERvcVulkanProductionFeature.None;
        bool descriptorHeap = rendererDescriptorBackend == ERvcDescriptorBackend.DescriptorHeap;
        bool descriptorIndexing = rendererDescriptorBackend == ERvcDescriptorBackend.DescriptorIndexing ||
            (rendererDescriptorBackend == ERvcDescriptorBackend.None && vulkan && settingsServices.EnableVulkanDescriptorIndexing);
        bool rendererVisibilityTargets = renderer?.SupportsRvcVisibilityTargets == true;
        bool openXrRuntimeFoveation = presentation.IsOpenXRActive && presentation.VrFoveationMode != EVrFoveationMode.Off;
        bool openXrQuadViews = presentation.IsOpenXRActive && settings.QuadViewEnabled;

        return new(
            ForwardPlusOracleAvailable: true,
            FrameGraphAvailable: true,
            VisibilityTargetsAvailable: rendererVisibilityTargets || openGl,
            VulkanBackend: vulkan,
            OpenGlBackend: openGl,
            DescriptorHeapSupported: descriptorHeap,
            DescriptorIndexingSupported: descriptorIndexing,
            FragmentShadingRateSupported: (rendererVulkanFeatures & ERvcVulkanProductionFeature.FragmentShadingRate) != 0,
            FragmentDensityMapSupported: (rendererVulkanFeatures & ERvcVulkanProductionFeature.FragmentDensityMap) != 0,
            OpenXrQuadViewsSupported: openXrQuadViews,
            OpenXrRuntimeFoveationSupported: openXrRuntimeFoveation,
            OpenXrDepthLayersSupported: false,
            OpenXrVisibilityMaskSupported: presentation.RvcOpenXrVisibilityMaskEnabled && (renderer?.SupportsRvcOpenXrVisibilityMaskStencil != false || openGl),
            MultiviewSupported: settings.QuadViewEnabled ? false : (rendererVulkanFeatures & ERvcVulkanProductionFeature.Multiview) != 0,
            StaticMeshVisibilitySourceSupported: renderer?.SupportsRvcStaticMeshVisibilitySource == true || openGl,
            SkinnedComputeVisibilitySourceSupported: renderer?.SupportsRvcSkinnedComputeVisibilitySource == true,
            ZeroReadbackIndirectVisibilitySourceSupported: renderer?.SupportsRvcZeroReadbackIndirectVisibilitySource == true,
            MeshletVisibilitySourceSupported: renderer?.SupportsRvcMeshletVisibilitySource == true,
            VulkanDynamicRenderingSupported: (rendererVulkanFeatures & ERvcVulkanProductionFeature.DynamicRendering) != 0,
            VulkanSynchronization2Supported: (rendererVulkanFeatures & ERvcVulkanProductionFeature.Synchronization2) != 0,
            VulkanMeshShaderSupported: (rendererVulkanFeatures & ERvcVulkanProductionFeature.MeshShader) != 0,
            VulkanTimelineSemaphoreSupported: (rendererVulkanFeatures & ERvcVulkanProductionFeature.TimelineSemaphore) != 0);
    }

    private static void ReportFallbackIfNeeded(in RvcPipelineResolution resolution)
    {
        if (resolution.FallbackReason == ERvcFallbackReason.None)
            return;

        Debug.RenderingWarningEvery(
            "RVC.PipelineFallback",
            TimeSpan.FromSeconds(5),
            "[RVC] requested={0} effective={1} active={2} fallback={3}: {4}",
            resolution.RequestedMode,
            resolution.EffectiveMode,
            resolution.IsRvcActive,
            resolution.FallbackReason,
            resolution.Diagnostic);
    }
}
