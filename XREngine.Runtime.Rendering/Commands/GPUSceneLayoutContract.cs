using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// CPU authority for the byte layout of records published by <see cref="GPUScene"/>.
/// These checks protect the std430 and indirect-command ABI at scene/pass initialization;
/// they are deliberately not part of a frame path.
/// </summary>
public static class GPUSceneLayoutContract
{
    // These describe the currently published ABI, not a target layout for future redesign.
    public const int DrawMetadataSize = 64;
    public const int TransformGpuSize = 64;
    public const int BoundsGpuSize = 64;
    public const int MaterialStateGpuSize = 32;
    public const int CompatibilityCommandSize = 80;
    public const int CompatibilityCommandColdSize = 32;
    public const int MeshDataEntrySize = 16;
    public const int LodTableEntrySize = 48;
    public const int LodTransitionStateSize = 16;
    public const int MeshletRangeSize = 16;
    public const int MeshletDescriptorSize = 80;
    public const int MeshletTaskRecordSize = 16;
    public const int SortKeyEntrySize = 16;
    public const int BatchRangeEntrySize = 16;
    public const int ViewBatchClassificationSize = 32;

    /// <summary>Validates every shared GPUScene record before its buffers are created.</summary>
    public static void ValidateRuntimeLayout()
    {
        RequireSize<DrawMetadata>(DrawMetadataSize);
        RequireOffset<DrawMetadata>(nameof(DrawMetadata.DrawID), 0);
        RequireOffset<DrawMetadata>(nameof(DrawMetadata.TransformID), 16);
        RequireOffset<DrawMetadata>(nameof(DrawMetadata.Flags), 32);
        RequireOffset<DrawMetadata>(nameof(DrawMetadata.BoundsID), 60);

        RequireSize<TransformGpu>(TransformGpuSize);
        RequireOffset<TransformGpu>(nameof(TransformGpu.WorldMatrix), 0);

        RequireSize<BoundsGpu>(BoundsGpuSize);
        RequireOffset<BoundsGpu>(nameof(BoundsGpu.BoundingSphere), 0);
        RequireOffset<BoundsGpu>(nameof(BoundsGpu.AabbMin), 16);
        RequireOffset<BoundsGpu>(nameof(BoundsGpu.AabbMax), 32);
        RequireOffset<BoundsGpu>(nameof(BoundsGpu.BoundsVersion), 48);

        RequireSize<MaterialStateGpu>(MaterialStateGpuSize);
        RequireOffset<MaterialStateGpu>(nameof(MaterialStateGpu.StateClassID), 0);
        RequireOffset<MaterialStateGpu>(nameof(MaterialStateGpu.DescriptorStart), 20);
        RequireOffset<MaterialStateGpu>(nameof(MaterialStateGpu.Flags), 28);

        RequireSize<GPUIndirectRenderCommand>(CompatibilityCommandSize);
        RequireOffset<GPUIndirectRenderCommand>(nameof(GPUIndirectRenderCommand.BoundingSphere), 0);
        RequireOffset<GPUIndirectRenderCommand>(nameof(GPUIndirectRenderCommand.MeshID), 16);
        RequireOffset<GPUIndirectRenderCommand>(nameof(GPUIndirectRenderCommand.RenderDistance), 40);
        RequireOffset<GPUIndirectRenderCommand>(nameof(GPUIndirectRenderCommand.BoundsID), 72);
        RequireOffset<GPUIndirectRenderCommand>(nameof(GPUIndirectRenderCommand.Reserved1), 76);

        RequireSize<GPUIndirectRenderCommandHot>(CompatibilityCommandSize);
        RequireOffset<GPUIndirectRenderCommandHot>(nameof(GPUIndirectRenderCommandHot.BoundingSphere), 0);
        RequireOffset<GPUIndirectRenderCommandHot>(nameof(GPUIndirectRenderCommandHot.MeshID), 16);
        RequireOffset<GPUIndirectRenderCommandHot>(nameof(GPUIndirectRenderCommandHot.RenderDistance), 52);
        RequireOffset<GPUIndirectRenderCommandHot>(nameof(GPUIndirectRenderCommandHot.SourceCommandIndex), 56);
        RequireOffset<GPUIndirectRenderCommandHot>(nameof(GPUIndirectRenderCommandHot.BoundsID), 76);

        RequireSize<GPUIndirectRenderCommandCold>(CompatibilityCommandColdSize);
        RequireOffset<GPUIndirectRenderCommandCold>(nameof(GPUIndirectRenderCommandCold.RenderIdentityID), 0);
        RequireOffset<GPUIndirectRenderCommandCold>(nameof(GPUIndirectRenderCommandCold.RenderDistance), 4);
        RequireOffset<GPUIndirectRenderCommandCold>(nameof(GPUIndirectRenderCommandCold.BoundsID), 24);

        RequireSize<GPUScene.MeshDataEntry>(MeshDataEntrySize);
        RequireSize<GPUScene.LODTableEntry>(LodTableEntrySize);
        RequireOffset<GPUScene.LODTableEntry>(nameof(GPUScene.LODTableEntry.LOD0_MinProjectedRadiusPixels), 20);
        RequireSize<GPUScene.GPULodTransitionState>(LodTransitionStateSize);
        RequireSize<GPUScene.GpuMeshletRange>(MeshletRangeSize);
        RequireSize<GPUScene.GpuMeshletDescriptor>(MeshletDescriptorSize);
        RequireOffset<GPUScene.GpuMeshletDescriptor>(nameof(GPUScene.GpuMeshletDescriptor.BoundsSphere), 0);
        RequireOffset<GPUScene.GpuMeshletDescriptor>(nameof(GPUScene.GpuMeshletDescriptor.Cone), 32);
        RequireOffset<GPUScene.GpuMeshletDescriptor>(nameof(GPUScene.GpuMeshletDescriptor.PackedCone), 64);

        RequireSize<GpuMeshletTaskRecord>(MeshletTaskRecordSize);
        RequireSize<GPUSortKeyEntry>(SortKeyEntrySize);
        RequireSize<GPUBatchRangeEntry>(BatchRangeEntrySize);
        RequireSize<GPUViewBatchClassification>(ViewBatchClassificationSize);
        RequireOffset<GPUViewBatchClassification>(nameof(GPUViewBatchClassification.DrawID), 24);
    }

    private static void RequireSize<T>(int expected) where T : unmanaged
    {
        int unsafeSize = Unsafe.SizeOf<T>();
        int marshalSize = Marshal.SizeOf<T>();
        if (unsafeSize != expected || marshalSize != expected)
            throw new InvalidOperationException($"{typeof(T).Name} ABI size mismatch: Unsafe.SizeOf={unsafeSize}, Marshal.SizeOf={marshalSize}, expected={expected}.");
    }

    private static void RequireOffset<T>(string fieldName, int expected) where T : unmanaged
    {
        int actual = checked((int)Marshal.OffsetOf<T>(fieldName));
        if (actual != expected)
            throw new InvalidOperationException($"{typeof(T).Name}.{fieldName} ABI offset mismatch: actual={actual}, expected={expected}.");
    }
}
