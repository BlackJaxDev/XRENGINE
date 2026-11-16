using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private EAdvancedClassificationDebugView _classificationDebugView;
    private uint _classificationTileWidth = AdvancedClassificationTileDimensions.DefaultTileWidth;
    private uint _classificationTileHeight = AdvancedClassificationTileDimensions.DefaultTileHeight;

    /// <summary>
    /// Diagnostic visualization mode for GPU material work classification.
    /// </summary>
    public EAdvancedClassificationDebugView ClassificationDebugView
    {
        get => _classificationDebugView;
        set
        {
            if (!SetField(ref _classificationDebugView, value))
                return;
            InvalidateClassificationResourceProfile();
        }
    }

    /// <summary>
    /// Classification tile width in pixels (defaults to 16).
    /// </summary>
    public uint ClassificationTileWidth
    {
        get => _classificationTileWidth;
        set
        {
            if (value != AdvancedClassificationTileDimensions.DefaultTileWidth)
                throw new ArgumentOutOfRangeException(nameof(value), "The admitted classification shader uses 16-pixel tiles.");
            if (!SetField(ref _classificationTileWidth, value))
                return;
            InvalidateClassificationResourceProfile();
        }
    }

    /// <summary>
    /// Classification tile height in pixels (defaults to 16).
    /// </summary>
    public uint ClassificationTileHeight
    {
        get => _classificationTileHeight;
        set
        {
            if (value != AdvancedClassificationTileDimensions.DefaultTileHeight)
                throw new ArgumentOutOfRangeException(nameof(value), "The admitted classification shader uses 16-pixel tiles.");
            if (!SetField(ref _classificationTileHeight, value))
                return;
            InvalidateClassificationResourceProfile();
        }
    }

    private void InvalidateClassificationResourceProfile()
        => InvalidateOwnedInstancePhysicalResources("MaterialClassificationProfileChanged");

    /// <summary>
    /// Maximum number of active tiles supported in storage buffer (64K tiles covers 4K stereo at 16x16).
    /// </summary>
    public const uint DefaultActiveTileCapacity = 65536u;

    /// <summary>
    /// Historical minimum membership capacity; physical storage is sized per kernel and output extent.
    /// </summary>
    public const uint DefaultKernelTileCapacity = 65536u;

    /// <summary>
    /// Maximum number of unique indirect compute dispatch arguments (one per active shading kernel).
    /// </summary>
    public const uint DefaultMaxShadingKernels = 128u;

    /// <summary>
    /// Number of per-kernel ranges in the kernel-tile membership buffer. Each range holds one
    /// record per tile, and kernel k owns range k, so this bounds the dense kernel indices that
    /// classification can store. The kernel table gives a new kernel the lowest free dense
    /// index, and at most <see cref="Commands.AdvancedGpuMaterialPublisher.MaxCanonicalKernelKeys"/>
    /// kernels are live, so the extra ranges cover kernels whose release still waits for the GPU.
    /// A kernel index past the last range sets the membership overflow flag, and the frame then
    /// shades every pixel in the overflow repair pass (correct output, more GPU time).
    /// With 128 ranges the buffer was about 144 MB per frame slot for a 2688x2688 eye pair.
    /// </summary>
    public const uint KernelTileRangeCount = Commands.AdvancedGpuMaterialPublisher.MaxCanonicalKernelKeys + 6u;

    private void DeclareClassificationResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint layers = Math.Max(builder.Profile.ViewCount, builder.Profile.Stereo ? 2u : 1u);
        uint tileCapacity = checked(
            AdvancedClassificationTileDimensions.CalculateTilesX(Math.Max(1u, builder.Profile.InternalWidth)) *
            AdvancedClassificationTileDimensions.CalculateTilesY(Math.Max(1u, builder.Profile.InternalHeight)) * layers);
        uint membershipCapacity = checked(tileCapacity * KernelTileRangeCount);

        for (uint slot = 0u; slot < AdvancedFrameSlotContract.DefaultSlotCount; slot++)
        {
            // 1. Active Tiles Buffer
            VisibilityBuffer<AdvancedActiveTileRecord>(
                    builder,
                    AdvancedClassificationResourceNames.ActiveTiles(slot),
                    tileCapacity,
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced active tiles slot {slot}")
                .Add();

            // 2. Kernel-Tile Memberships Buffer
            VisibilityBuffer<AdvancedKernelTileRecord>(
                    builder,
                    AdvancedClassificationResourceNames.KernelTiles(slot),
                    membershipCapacity,
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced kernel-tile memberships slot {slot}")
                .Add();

            // 3. Indirect Compute Dispatch Arguments
            VisibilityBuffer<AdvancedClassificationDispatchArguments>(
                    builder,
                    AdvancedClassificationResourceNames.DispatchArgs(slot),
                    DefaultMaxShadingKernels,
                    EBufferTarget.DispatchIndirectBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced classification indirect dispatch args slot {slot}")
                .Add();

            VisibilityBuffer<uint>(builder,
                    AdvancedClassificationResourceNames.KernelCounts(slot),
                    DefaultMaxShadingKernels,
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced classification kernel range counts slot {slot}")
                .Add();

            // 4. GPU-Atomic Classification Counters
            VisibilityBuffer<AdvancedClassificationGpuCounters>(
                    builder,
                    AdvancedClassificationResourceNames.Counters(slot),
                    1u,
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced classification counters slot {slot}")
                .Add();
        }

        // 5. Optional Debug Visualization Image
        ReconstructionTexture(
                builder,
                AdvancedClassificationResourceNames.DebugOutput,
                internalSize,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte,
                ESizedInternalFormat.Rgba8)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .When(static profile => HasClassificationFeature(profile, AdvancedClassificationResourceFeature.DebugOutput))
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata)
            .DebugLabel("Advanced material classification debug visualization")
            .Add();
    }

    private static bool HasClassificationFeature(
        RenderPipelineResourceProfile profile,
        AdvancedClassificationResourceFeature feature)
        => ((AdvancedClassificationResourceFeature)(profile.FeatureMask >> 32) & feature) != 0;
}
