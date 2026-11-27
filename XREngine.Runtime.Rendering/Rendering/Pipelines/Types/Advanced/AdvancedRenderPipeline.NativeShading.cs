using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private const ulong WebAmbientOcclusionDisabledFeatureBit = 1UL << 41;

    internal bool UsesWebAmbientOcclusion
        => RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.EnableGtao &&
           EnableBuiltInAmbientOcclusion && AmbientOcclusionProvider is not null;

    private EAdvancedShadingDebugView _shadingDebugView;
    private uint _froxelDepthSlices = AdvancedFroxelGridDimensions.DefaultDepthSlices;

    /// <summary>
    /// Process-wide shading debug view from <see cref="XREngineEnvironmentVariables.AdvancedShadingDebugView"/>,
    /// or null when the variable is unset or invalid. Read once at type initialization.
    /// </summary>
    private static readonly EAdvancedShadingDebugView? s_shadingDebugViewOverride = ResolveShadingDebugViewOverride();

    private static EAdvancedShadingDebugView? ResolveShadingDebugViewOverride()
    {
        string? value = XREnvironment.GetValue(XREngineEnvironmentVariables.AdvancedShadingDebugView);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Enum.TryParse(value.Trim(), ignoreCase: true, out EAdvancedShadingDebugView view) &&
               Enum.IsDefined(view)
            ? view
            : null;
    }

    /// <summary>
    /// Diagnostic visualization mode for native opaque shading and clustered lighting. When
    /// <see cref="XREngineEnvironmentVariables.AdvancedShadingDebugView"/> is set, every Advanced
    /// stage family (desktop, per-eye, stereo and RVC) returns that view instead of its own value,
    /// so one launch shows the same view in every output.
    /// </summary>
    public EAdvancedShadingDebugView ShadingDebugView
    {
        get => s_shadingDebugViewOverride ?? _shadingDebugView;
        set
        {
            if (!SetField(ref _shadingDebugView, value))
                return;
            InvalidateNativeShadingResourceProfile();
        }
    }

    /// <summary>
    /// Returns the shading debug view that native opaque shading uses for one camera.
    /// The pipeline's <see cref="ShadingDebugView"/> wins when it selects a view. When it
    /// selects none and the camera's meshlet debug display is on (the camera's runtime
    /// override or its <see cref="GpuBvhDebugSettings"/> post-process stage), this returns
    /// <see cref="EAdvancedShadingDebugView.MeshletId"/>.
    /// </summary>
    public EAdvancedShadingDebugView ResolveShadingDebugView(XRCamera? camera)
    {
        EAdvancedShadingDebugView view = ShadingDebugView;
        return view == EAdvancedShadingDebugView.Disabled &&
            GpuBvhDebugSettings.IsMeshletDebugDisplayEnabled(camera, this)
                ? EAdvancedShadingDebugView.MeshletId
                : view;
    }

    /// <summary>
    /// Selects the mesh submission strategy that an Advanced output captures in its frame
    /// package. The meshlet ID view colors each pixel by the meshlet that drew it. Only the
    /// meshlet producers write a meshlet index into the visibility identity; the indexed
    /// producers write a triangle index, so with an indexed strategy every opaque pixel is
    /// neutral grey. While the camera shows the meshlet ID view on a Vulkan renderer that
    /// reported a meshlet path, this replaces a GPU indexed strategy with a GPU meshlet
    /// strategy, so the visibility raster draws through the meshlet producers. Other
    /// outputs and the process-wide strategy do not change.
    /// </summary>
    /// <param name="pipeline">The output's pipeline. Only a pipeline that runs the Advanced stage family is changed.</param>
    /// <param name="camera">The output's camera, which owns the meshlet debug display toggle.</param>
    /// <param name="requested">The strategy that the output requests without the meshlet view.</param>
    /// <returns>
    /// <paramref name="requested"/> for CPU-direct submission, for a requested meshlet strategy,
    /// for a pipeline without the Advanced stage family, and when no meshlet path is available.
    /// The view then stays grey, and the pipeline editor states why.
    /// </returns>
    public static EMeshSubmissionStrategy ResolveMeshletViewSubmissionStrategy(
        RenderPipeline? pipeline,
        XRCamera? camera,
        EMeshSubmissionStrategy requested)
    {
        if (requested == EMeshSubmissionStrategy.CpuDirect || requested.IsAnyMeshletStrategy())
            return requested;

        if (pipeline is not IAdvancedRenderStageFamilyHost { UsesAdvancedStageFamily: true } familyHost ||
            familyHost.AdvancedStageFamilyDefinition.ResolveShadingDebugView(camera) != EAdvancedShadingDebugView.MeshletId)
            return requested;

        // Only the Vulkan Advanced path rasterizes the meshlet producers. The collect-visible
        // thread captures the package and can run before a renderer is current, so this
        // reads the renderer capability that the last strategy resolution recorded.
        if (RuntimeEngine.Rendering.LastResolvedRendererBackend != RuntimeGraphicsApiKind.Vulkan ||
            !RuntimeEngine.Rendering.LastResolvedSupportsMeshletDispatch)
            return requested;

        return GpuBvhDebugSettings.ResolveMeshletDebugDisplayStrategy();
    }

    /// <summary>
    /// Number of exponential depth slices along the view frustum (defaults to 24).
    /// </summary>
    public uint FroxelDepthSlices
    {
        get => _froxelDepthSlices;
        set
        {
            if (!SetField(ref _froxelDepthSlices, Math.Max(1u, value)))
                return;
            InvalidateNativeShadingResourceProfile();
        }
    }

    /// <summary>
    /// True when this family declares the RVC shadelet telemetry counters. Only
    /// <see cref="RvcRenderPipeline"/> sets it, before the family builds resources.
    /// </summary>
    internal bool DeclaresRvcShadeletTelemetry { get; set; }

    /// <summary>
    /// True when this family declares the RVC shadelet-cache GPU buffers and pixel-to-shadelet map.
    /// Set by <see cref="RvcRenderPipeline"/> when an active RVC cache mode is resolved.
    /// </summary>
    internal bool DeclaresRvcShadeletCache { get; set; }

    /// <summary>
    /// Optional callback supplied by a host pipeline (such as <see cref="RvcRenderPipeline"/>)
    /// so profile invalidations on this inner stage family also invalidate host instances.
    /// </summary>
    internal Action<string>? HostedPhysicalResourceInvalidator { get; set; }

    private void InvalidateNativeShadingResourceProfile()
    {
        InvalidateOwnedInstancePhysicalResources("NativeShadingProfileChanged");
        HostedPhysicalResourceInvalidator?.Invoke("NativeShadingProfileChanged");
    }

    /// <summary>
    /// Historical minimum; physical froxel storage is derived from the output extent and view count.
    /// </summary>
    public const uint DefaultFroxelCapacity = 262144u;

    /// <summary>
    /// Maximum capacity of the light index list buffer (1,048,576 indices).
    /// </summary>
    public const uint DefaultLightIndexListCapacity = 1048576u;

    private IAdvancedAmbientOcclusionProvider? _ambientOcclusionProvider = AdvancedDepthGtaoProvider.Instance;
    private bool _enableBuiltInAmbientOcclusion = true;

    /// <summary>
    /// Enables the built-in depth-derived GTAO dispatch on browser outputs. Disabled browser
    /// outputs use the shared neutral binding. Desktop outputs write neutral AO in this stage,
    /// and the AO mode from the camera's schema then replaces it.
    /// </summary>
    public bool EnableBuiltInAmbientOcclusion
    {
        get => _enableBuiltInAmbientOcclusion;
        set
        {
            if (!SetField(ref _enableBuiltInAmbientOcclusion, value))
                return;
            if (Shaders.Compilation.WebPipelineRasterProgram.IsActive)
                RebuildCommandChain();
            InvalidateNativeShadingResourceProfile();
        }
    }

    /// <summary>
    /// Active ambient occlusion provider contract.
    /// </summary>
    public IAdvancedAmbientOcclusionProvider? AmbientOcclusionProvider
    {
        get => _ambientOcclusionProvider;
        set
        {
            if (!SetField(ref _ambientOcclusionProvider, value))
                return;
            if (Shaders.Compilation.WebPipelineRasterProgram.IsActive)
                RebuildCommandChain();
            InvalidateNativeShadingResourceProfile();
        }
    }

    /// <summary>
    /// Maximum capacity of the decal index list buffer (65,536 indices).
    /// </summary>
    public const uint DefaultDecalIndexListCapacity = 65536u;

    private void DeclareNativeShadingResources(RenderPipelineResourceLayoutBuilder builder)
    {
        if (Shaders.Compilation.WebPipelineRasterProgram.IsActive)
            DeclareAdvancedWebNeutralResource(builder);
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint layers = Math.Max(builder.Profile.ViewCount, builder.Profile.Stereo ? 2u : 1u);
        uint froxelViewCount = AdvancedFroxelGridDimensions.CalculateTotalViewCapacity(
            layers,
            DecodePortalFroxelSlotCount(builder.Profile));
        uint froxelCapacity = checked(
            AdvancedFroxelGridDimensions.CalculateTilesX(Math.Max(1u, builder.Profile.InternalWidth)) *
            AdvancedFroxelGridDimensions.CalculateTilesY(Math.Max(1u, builder.Profile.InternalHeight)) *
            FroxelDepthSlices * froxelViewCount);

        for (uint slot = 0u; slot < AdvancedFrameSlotContract.DefaultSlotCount; slot++)
        {
            // 1. Froxel Grid Buffer
            VisibilityBuffer<AdvancedFroxelRecord>(
                    builder,
                    AdvancedClusteredLightingResourceNames.FroxelGrid(slot),
                    froxelCapacity,
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced froxel grid slot {slot}")
                .Add();

            // 2. Light Index List Buffer
            VisibilityBuffer<uint>(
                    builder,
                    AdvancedClusteredLightingResourceNames.LightIndexList(slot),
                    DefaultLightIndexListCapacity,
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced clustered light index list slot {slot}")
                .Add();

            // 3. Froxel Decal Grid Buffer
            VisibilityBuffer<AdvancedFroxelDecalRecord>(
                    builder,
                    AdvancedClusteredLightingResourceNames.FroxelDecalGrid(slot),
                    froxelCapacity,
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced froxel decal grid slot {slot}")
                .Add();

            // 4. Decal Index List Buffer
            VisibilityBuffer<uint>(
                    builder,
                    AdvancedClusteredLightingResourceNames.DecalIndexList(slot),
                    DefaultDecalIndexListCapacity,
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced decal index list slot {slot}")
                .Add();

            VisibilityBuffer<uint>(builder,
                    AdvancedClusteredLightingResourceNames.LightingCounters(slot),
                    4u,
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced light/decal-index allocation and overflow counters slot {slot}")
                .Add();

            if (DeclaresRvcShadeletTelemetry)
            {
                VisibilityBuffer<uint>(builder,
                        RvcShadeletTelemetryResourceNames.Counters(slot),
                        (uint)RvcShadeletTelemetryCounters.GpuWordCount,
                        EBufferTarget.ShaderStorageBuffer,
                        EBufferUsage.DynamicRead)
                    .Lifetime(RenderResourceLifetime.Transient)
                    .DebugLabel($"RVC shadelet telemetry counters slot {slot}")
                    .Add();
            }

            if (DeclaresRvcShadeletCache)
            {
                VisibilityBuffer<uint>(builder,
                        RvcShadeletCacheResourceNames.Records(slot),
                        checked(RvcShadeletCacheResourceNames.DefaultMaxShadelets * (RvcShadeletCacheResourceNames.RecordByteStride / sizeof(uint))),
                        EBufferTarget.ShaderStorageBuffer,
                        EBufferUsage.DynamicRead)
                    .Lifetime(RenderResourceLifetime.Transient)
                    .DebugLabel($"RVC shadelet records slot {slot}")
                    .Add();

                VisibilityBuffer<uint>(builder,
                        RvcShadeletCacheResourceNames.BinIndices(slot),
                        RvcShadeletCacheResourceNames.DefaultMaxShadelets,
                        EBufferTarget.ShaderStorageBuffer,
                        EBufferUsage.DynamicRead)
                    .Lifetime(RenderResourceLifetime.Transient)
                    .DebugLabel($"RVC shadelet bin indices slot {slot}")
                    .Add();

                VisibilityBuffer<uint>(builder,
                        RvcShadeletCacheResourceNames.ShadedOutputs(slot),
                        checked(RvcShadeletCacheResourceNames.DefaultMaxShadelets * (RvcShadeletCacheResourceNames.ShadedOutputByteStride / sizeof(uint))),
                        EBufferTarget.ShaderStorageBuffer,
                        EBufferUsage.DynamicRead)
                    .Lifetime(RenderResourceLifetime.Transient)
                    .DebugLabel($"RVC shadelet shaded outputs slot {slot}")
                    .Add();

                VisibilityBuffer<uint>(builder,
                        RvcShadeletCacheResourceNames.DispatchAndCounters(slot),
                        (uint)RvcShadeletCacheCounters.GpuWordCount,
                        EBufferTarget.DispatchIndirectBuffer,
                        EBufferUsage.DynamicRead)
                    .Lifetime(RenderResourceLifetime.Transient)
                    .DebugLabel($"RVC shadelet indirect dispatch and counters slot {slot}")
                    .Add();
            }
        }

        if (DeclaresRvcShadeletCache)
        {
            ReconstructionTexture(
                    builder,
                    RvcShadeletCacheResourceNames.PixelToShadelet,
                    internalSize,
                    EPixelInternalFormat.R32ui,
                    EPixelFormat.RedInteger,
                    EPixelType.UnsignedInt,
                    ESizedInternalFormat.R32ui)
                .Layers(layers)
                .StereoCompatible(layers > 1u)
                .DependsOn(
                    AdvancedVisibilityResourceNames.Identity,
                    AdvancedVisibilityResourceNames.Metadata,
                    AdvancedVisibilityResourceNames.DepthStencil)
                .DebugLabel("RVC pixel-to-shadelet map")
                .Add();
        }

        // 5. Native HDR Scene Output Texture
        ReconstructionTexture(
                builder,
                HDRSceneTextureName,
                internalSize,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat,
                ESizedInternalFormat.Rgba16f)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata,
                AdvancedVisibilityResourceNames.DepthStencil)
            .DebugLabel("Advanced native HDR scene texture")
            .Add();

        // 6. Native Motion Vectors Texture
        ReconstructionTexture(
                builder,
                VelocityTextureName,
                internalSize,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat,
                ESizedInternalFormat.Rg16f)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .Usage(RenderPipelineResourceUsage.SampledTexture |
                RenderPipelineResourceUsage.StorageImage |
                RenderPipelineResourceUsage.TransferSource |
                RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata,
                AdvancedVisibilityResourceNames.DepthStencil)
            .DebugLabel("Advanced native motion vectors")
            .Add();

        // Native shading writes disocclusion guidance even for outputs without
        // temporal accumulation or late transparency, such as HDR thumbnails.
        ReconstructionTexture(
                builder,
                AdvancedTemporalHistoryContract.ReactiveMaskResourceName,
                internalSize,
                EPixelInternalFormat.R8,
                EPixelFormat.Red,
                EPixelType.UnsignedByte,
                ESizedInternalFormat.R8)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .Usage(RenderPipelineResourceUsage.SampledTexture |
                RenderPipelineResourceUsage.StorageImage |
                RenderPipelineResourceUsage.TransferSource |
                RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata)
            .DebugLabel("Advanced reactive temporal mask")
            .Add();

        // 7. Native Shading Diagnostics Image
        ReconstructionTexture(
                builder,
                AdvancedShadingResourceNames.ShadingDiagnostics,
                internalSize,
                EPixelInternalFormat.R32ui,
                EPixelFormat.RedInteger,
                EPixelType.UnsignedInt,
                ESizedInternalFormat.R32ui)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata,
                AdvancedVisibilityResourceNames.DepthStencil)
            .When(static profile => (profile.FeatureMask & ShadingDiagnosticsOmittedFeatureBit) == 0UL)
            .DebugLabel("Advanced native shading diagnostics")
            .Add();

        // 7b. Shading diagnostics placeholder. An output that omits the diagnostics image (OpenXR
        // eyes without a shading debug view) still needs an image at the diagnostics binding. A
        // dedicated 1x1 image, created with the resource generation, fills it. It has the same
        // layer count, so each view gets its own layer view like the real image. The shading push
        // flag stops every store to it, and no other binding refers to it, so sync validation sees
        // no hazard against the visibility inputs.
        ReconstructionTexture(
                builder,
                AdvancedShadingResourceNames.ShadingDiagnosticsPlaceholder,
                RenderResourceSizePolicy.Absolute(1u, 1u),
                EPixelInternalFormat.R32ui,
                EPixelFormat.RedInteger,
                EPixelType.UnsignedInt,
                ESizedInternalFormat.R32ui)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .When(static profile => (profile.FeatureMask & ShadingDiagnosticsOmittedFeatureBit) != 0UL)
            .DebugLabel("Advanced native shading diagnostics placeholder")
            .Add();

        // 8. Ambient Occlusion Output Texture
        ReconstructionTexture(
                builder,
                AdvancedAmbientOcclusionContract.ResourceName,
                internalSize,
                EPixelInternalFormat.R8,
                EPixelFormat.Red,
                EPixelType.UnsignedByte,
                ESizedInternalFormat.R8)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage |
                RenderPipelineResourceUsage.ColorAttachment)
            .When(static profile => (profile.FeatureMask & WebAmbientOcclusionDisabledFeatureBit) == 0)
            .DependsOn(
                AdvancedVisibilityResourceNames.DepthStencil)
            .DebugLabel("Advanced ambient occlusion")
            .Add();

        // 9. Optional Shading Debug Visualization Image
        ReconstructionTexture(
                builder,
                AdvancedShadingResourceNames.ShadingDebugOutput,
                internalSize,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.Float,
                ESizedInternalFormat.Rgba16f)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .When(static profile => ((profile.FeatureMask >> 40) & 1u) != 0)
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata)
            .DebugLabel("Advanced native shading debug visualization")
            .Add();

        DeclareDecalBufferResources(builder, internalSize, layers);
    }

    /// <summary>
    /// Mesh surface decal buffer: two single-sample RGBA8 targets and their framebuffer with the
    /// resolved visibility depth (read-only during the decal pass).
    /// </summary>
    private void DeclareDecalBufferResources(
        RenderPipelineResourceLayoutBuilder builder,
        RenderResourceSizePolicy internalSize,
        uint layers)
    {
        VisibilityTexture(
                builder,
                AdvancedDecalBufferResourceNames.Albedo,
                internalSize,
                RenderPipelineResourceUsage.ColorAttachment |
                RenderPipelineResourceUsage.SampledTexture,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte,
                ESizedInternalFormat.Rgba8,
                EFrameBufferAttachment.ColorAttachment0,
                storage: false)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .DependsOn(AdvancedVisibilityResourceNames.DepthStencil)
            .DebugLabel("Advanced surface decal buffer albedo")
            .Add();

        VisibilityTexture(
                builder,
                AdvancedDecalBufferResourceNames.Surface,
                internalSize,
                RenderPipelineResourceUsage.ColorAttachment |
                RenderPipelineResourceUsage.SampledTexture,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte,
                ESizedInternalFormat.Rgba8,
                EFrameBufferAttachment.ColorAttachment1,
                storage: false)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .DependsOn(AdvancedDecalBufferResourceNames.Albedo)
            .DebugLabel("Advanced surface decal buffer roughness/metallic")
            .Add();

        builder.FrameBuffer(AdvancedDecalBufferResourceNames.FrameBuffer)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(internalSize)
            .Color(0, AdvancedDecalBufferResourceNames.Albedo, layerIndex: -1)
            .Color(1, AdvancedDecalBufferResourceNames.Surface, layerIndex: -1)
            .DepthStencil(AdvancedVisibilityResourceNames.DepthStencil, layerIndex: -1)
            .Factory(CreateDecalBufferFrameBuffer)
            .DebugLabel("Advanced surface decal buffer FBO")
            .Add();
    }

    private XRFrameBuffer CreateDecalBufferFrameBuffer()
        => new(
            (RequireVisibilityAttachment(AdvancedDecalBufferResourceNames.Albedo), EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (RequireVisibilityAttachment(AdvancedDecalBufferResourceNames.Surface), EFrameBufferAttachment.ColorAttachment1, 0, -1),
            (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.DepthStencil), EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = AdvancedDecalBufferResourceNames.FrameBuffer,
        };
}
