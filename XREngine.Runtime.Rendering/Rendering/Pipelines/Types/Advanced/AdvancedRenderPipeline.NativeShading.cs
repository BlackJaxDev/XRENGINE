using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private EAdvancedShadingDebugView _shadingDebugView;
    private uint _froxelDepthSlices = AdvancedFroxelGridDimensions.DefaultDepthSlices;

    /// <summary>
    /// Diagnostic visualization mode for native opaque shading and clustered lighting.
    /// </summary>
    public EAdvancedShadingDebugView ShadingDebugView
    {
        get => _shadingDebugView;
        set
        {
            if (!SetField(ref _shadingDebugView, value))
                return;
            InvalidateNativeShadingResourceProfile();
        }
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

    private void InvalidateNativeShadingResourceProfile()
        => InvalidateOwnedInstancePhysicalResources("NativeShadingProfileChanged");

    /// <summary>
    /// Maximum number of froxel records per frame slot (262,144 covers 1440p stereo or 4K mono at 24 depth slices).
    /// </summary>
    public const uint DefaultFroxelCapacity = 262144u;

    /// <summary>
    /// Maximum capacity of the light index list buffer (1,048,576 indices).
    /// </summary>
    public const uint DefaultLightIndexListCapacity = 1048576u;

    private IAdvancedGlobalIlluminationProvider? _globalIlluminationProvider;
    private IAdvancedAmbientOcclusionProvider? _ambientOcclusionProvider;

    /// <summary>
    /// Active global illumination provider contract.
    /// </summary>
    public IAdvancedGlobalIlluminationProvider? GlobalIlluminationProvider
    {
        get => _globalIlluminationProvider;
        set
        {
            if (!SetField(ref _globalIlluminationProvider, value))
                return;
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
            InvalidateNativeShadingResourceProfile();
        }
    }

    /// <summary>
    /// Maximum capacity of the decal index list buffer (65,536 indices).
    /// </summary>
    public const uint DefaultDecalIndexListCapacity = 65536u;

    private void DeclareNativeShadingResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint layers = Math.Max(builder.Profile.ViewCount, builder.Profile.Stereo ? 2u : 1u);

        for (uint slot = 0u; slot < AdvancedFrameSlotContract.DefaultSlotCount; slot++)
        {
            // 1. Froxel Grid Buffer
            VisibilityBuffer<AdvancedFroxelRecord>(
                    builder,
                    AdvancedClusteredLightingResourceNames.FroxelGrid(slot),
                    DefaultFroxelCapacity,
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
                    DefaultFroxelCapacity,
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
                AdvancedVisibilityResourceNames.Metadata)
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
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata)
            .DebugLabel("Advanced native motion vectors")
            .Add();

        // 7. Ambient Occlusion Output Texture
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
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata)
            .DebugLabel("Advanced ambient occlusion")
            .Add();

        // 8. Optional Shading Debug Visualization Image
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
    }
}
