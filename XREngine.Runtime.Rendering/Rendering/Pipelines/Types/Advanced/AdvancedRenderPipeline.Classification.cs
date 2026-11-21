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
    /// Index of the overflow repair command in the classification dispatch arguments, after the
    /// per-kernel commands of every view. Classification writes the full tile grid of one view
    /// there when it overflowed and zero work groups otherwise, so a frame without overflow
    /// launches no repair groups. Each view's shading pass dispatches this command for its view.
    /// </summary>
    public static uint OverflowRepairDispatchIndex(uint viewCount)
        => checked(DefaultMaxShadingKernels * Math.Max(1u, viewCount));

    /// <summary>
    /// Number of commands in the classification dispatch arguments: one block of per-kernel
    /// commands for each view, then the overflow repair command. A single view keeps 129 commands.
    /// </summary>
    public static uint ClassificationDispatchCommandCount(uint viewCount)
        => checked(OverflowRepairDispatchIndex(viewCount) + 1u);

    /// <summary>
    /// Index of the indirect command of <paramref name="kernel"/> for <paramref name="viewIndex"/>.
    /// The command launches only the memberships that this view's classification added.
    /// </summary>
    public static uint KernelDispatchIndex(uint viewIndex, uint kernel)
        => checked(viewIndex * DefaultMaxShadingKernels + kernel);

    /// <summary>
    /// Number of words in the kernel count buffer. The first <see cref="DefaultMaxShadingKernels"/>
    /// words count the memberships of each kernel over the views classified so far. Then one block
    /// per view holds the count of each kernel after that view, which gives each view its range of
    /// the kernel's membership list (views classify in order with a barrier between them).
    /// </summary>
    public static uint KernelCountWordCount(uint viewCount)
        => checked(DefaultMaxShadingKernels * (Math.Max(1u, viewCount) + 1u));

    /// <summary>
    /// Number of per-kernel ranges in the kernel-tile membership buffer. Each range holds one
    /// record per tile, and kernel k owns range k, so this bounds the dense kernel indices that
    /// classification can store. The kernel table gives a new kernel the lowest free dense
    /// index, and at most <see cref="Commands.AdvancedGpuMaterialPublisher.MaxCanonicalKernelKeys"/>
    /// kernels are live, so the extra ranges cover kernels whose release still waits for the GPU.
    /// A kernel index past the last range sets the membership overflow flag, and the frame then
    /// shades every pixel in the overflow repair pass (correct output, more GPU time).
    /// For a 2688x2688 eye pair this is about 38 MB per frame slot instead of 101 MB.
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

            // 3. Indirect Compute Dispatch Arguments: one command per shading kernel and view, then
            // the overflow repair command (OverflowRepairDispatchIndex).
            VisibilityBuffer<AdvancedClassificationDispatchArguments>(
                    builder,
                    AdvancedClassificationResourceNames.DispatchArgs(slot),
                    ClassificationDispatchCommandCount(layers),
                    EBufferTarget.DispatchIndirectBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel($"Advanced classification indirect dispatch args slot {slot}")
                .Add();

            VisibilityBuffer<uint>(builder,
                    AdvancedClassificationResourceNames.KernelCounts(slot),
                    KernelCountWordCount(layers),
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
