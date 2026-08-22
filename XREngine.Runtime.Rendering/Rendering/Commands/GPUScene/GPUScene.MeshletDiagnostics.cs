namespace XREngine.Rendering.Commands;

public partial class GPUScene
{
    private const GPUIndirectRenderFlags MeshletIneligibleFlags =
        GPUIndirectRenderFlags.Transparent |
        GPUIndirectRenderFlags.Skinned |
        GPUIndirectRenderFlags.Dynamic |
        GPUIndirectRenderFlags.DoubleSided |
        GPUIndirectRenderFlags.Wireframe |
        GPUIndirectRenderFlags.Instanced |
        GPUIndirectRenderFlags.Animated |
        GPUIndirectRenderFlags.BlendShapes |
        GPUIndirectRenderFlags.CustomShader |
        GPUIndirectRenderFlags.CpuFallbackOnly |
        GPUIndirectRenderFlags.NonCanonicalRasterState;

    /// <summary>
    /// Classifies the stable render-side CPU metadata against the initial
    /// production meshlet route. This deliberately runs only when requested by
    /// diagnostics and does not inspect GPU-written visibility or LOD state.
    /// </summary>
    public GpuMeshletEligibilitySnapshot CaptureMeshletEligibilitySnapshot(int renderPass)
    {
        uint totalCommands = TotalCommandCount;
        uint activeCommands = 0u;
        uint passCommands = 0u;
        uint eligibleCommands = 0u;
        ulong eligibleMeshlets = 0ul;
        uint missingMetadata = 0u;
        uint rejectedInstanceCount = 0u;
        uint rejectedSkin = 0u;
        uint rejectedStateClass = 0u;
        uint rejectedFlags = 0u;
        uint missingMeshletRange = 0u;
        uint transparentFlags = 0u;
        uint skinnedFlags = 0u;
        uint dynamicTransformFlags = 0u;
        uint doubleSidedFlags = 0u;
        uint instancedFlags = 0u;
        uint animatedFlags = 0u;
        uint blendShapeFlags = 0u;
        uint customShaderFlags = 0u;
        uint cpuFallbackOnlyFlags = 0u;
        uint nonCanonicalRasterStateFlags = 0u;

        using (_lock.EnterScope())
        {
            for (uint commandIndex = 0u; commandIndex < totalCommands; ++commandIndex)
            {
                if (_allLoadedDrawMetadataBuffer is null ||
                    commandIndex >= _allLoadedDrawMetadataBuffer.ElementCount)
                {
                    ++missingMetadata;
                    continue;
                }

                DrawMetadata metadata = _allLoadedDrawMetadataBuffer
                    .GetDataRawAtIndex<DrawMetadata>(commandIndex);
                if (metadata.InstanceCount == 0u)
                    continue;

                ++activeCommands;
                if (metadata.RenderPass != unchecked((uint)renderPass) &&
                    metadata.RenderPass != uint.MaxValue)
                {
                    continue;
                }

                ++passCommands;
                GPUIndirectRenderFlags flags = (GPUIndirectRenderFlags)metadata.Flags;
                transparentFlags += HasFlag(flags, GPUIndirectRenderFlags.Transparent);
                skinnedFlags += HasFlag(flags, GPUIndirectRenderFlags.Skinned);
                dynamicTransformFlags += HasFlag(flags, GPUIndirectRenderFlags.Dynamic);
                doubleSidedFlags += HasFlag(flags, GPUIndirectRenderFlags.DoubleSided);
                instancedFlags += HasFlag(flags, GPUIndirectRenderFlags.Instanced);
                animatedFlags += HasFlag(flags, GPUIndirectRenderFlags.Animated);
                blendShapeFlags += HasFlag(flags, GPUIndirectRenderFlags.BlendShapes);
                customShaderFlags += HasFlag(flags, GPUIndirectRenderFlags.CustomShader);
                cpuFallbackOnlyFlags += HasFlag(flags, GPUIndirectRenderFlags.CpuFallbackOnly);
                nonCanonicalRasterStateFlags += HasFlag(flags, GPUIndirectRenderFlags.NonCanonicalRasterState);

                if (metadata.SkinID != 0u)
                {
                    ++rejectedSkin;
                    continue;
                }

                if (metadata.InstanceCount != 1u)
                {
                    ++rejectedInstanceCount;
                    continue;
                }

                if (metadata.StateClassID != (uint)EGpuMaterialStateClass.OpaqueDeferred)
                {
                    ++rejectedStateClass;
                    continue;
                }

                if ((flags & MeshletIneligibleFlags) != 0)
                {
                    ++rejectedFlags;
                    continue;
                }

                if (!_meshletRangesByMeshId.TryGetValue(metadata.MeshID, out GpuMeshletRange range) ||
                    !range.HasMeshlets)
                {
                    ++missingMeshletRange;
                    continue;
                }

                ++eligibleCommands;
                eligibleMeshlets += range.MeshletCount;
            }
        }

        return new GpuMeshletEligibilitySnapshot(
            totalCommands,
            activeCommands,
            passCommands,
            eligibleCommands,
            eligibleMeshlets,
            missingMetadata,
            rejectedInstanceCount,
            rejectedSkin,
            rejectedStateClass,
            rejectedFlags,
            missingMeshletRange,
            transparentFlags,
            skinnedFlags,
            dynamicTransformFlags,
            doubleSidedFlags,
            instancedFlags,
            animatedFlags,
            blendShapeFlags,
            customShaderFlags,
            cpuFallbackOnlyFlags,
            nonCanonicalRasterStateFlags);
    }

    private static uint HasFlag(GPUIndirectRenderFlags value, GPUIndirectRenderFlags flag)
        => (value & flag) != 0 ? 1u : 0u;
}
