using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Verifies that GPU scene records have the same managed and interop sizes.
/// Shader member offsets and strides are checked against compiled SPIR-V by
/// the editor before shader packaging.
/// </summary>
public static class GPUSceneLayoutContract
{
    public static int DrawMetadataSize => Unsafe.SizeOf<DrawMetadata>();
    public static int TransformGpuSize => Unsafe.SizeOf<TransformGpu>();
    public static int BoundsGpuSize => Unsafe.SizeOf<BoundsGpu>();
    public static int MaterialStateGpuSize => Unsafe.SizeOf<MaterialStateGpu>();
    public static int MeshDataEntrySize => Unsafe.SizeOf<GPUScene.MeshDataEntry>();
    public static int LodTableEntrySize => Unsafe.SizeOf<GPUScene.LODTableEntry>();
    public static int LodTransitionStateSize => Unsafe.SizeOf<GPUScene.GPULodTransitionState>();
    public static int MeshletRangeSize => Unsafe.SizeOf<GPUScene.GpuMeshletRange>();
    public static int MeshletDescriptorSize => Unsafe.SizeOf<GPUScene.GpuMeshletDescriptor>();
    public static int MeshletTaskRecordSize => Unsafe.SizeOf<GpuMeshletTaskRecord>();
    public static int SortKeyEntrySize => Unsafe.SizeOf<GPUSortKeyEntry>();
    public static int BatchRangeEntrySize => Unsafe.SizeOf<GPUBatchRangeEntry>();
    public static int ViewBatchClassificationSize => Unsafe.SizeOf<GPUViewBatchClassification>();

    public static void ValidateRuntimeLayout()
    {
        RequireMatchingSize<DrawMetadata>();
        RequireMatchingSize<TransformGpu>();
        RequireMatchingSize<BoundsGpu>();
        RequireMatchingSize<MaterialStateGpu>();
        RequireMatchingSize<GPUScene.MeshDataEntry>();
        RequireMatchingSize<GPUScene.LODTableEntry>();
        RequireMatchingSize<GPUScene.GPULodTransitionState>();
        RequireMatchingSize<GPUScene.GpuMeshletRange>();
        RequireMatchingSize<GPUScene.GpuMeshletDescriptor>();
        RequireMatchingSize<GpuMeshletTaskRecord>();
        RequireMatchingSize<GPUSortKeyEntry>();
        RequireMatchingSize<GPUBatchRangeEntry>();
        RequireMatchingSize<GPUViewBatchClassification>();
    }

    private static void RequireMatchingSize<T>() where T : unmanaged
    {
        int unsafeSize = Unsafe.SizeOf<T>();
        int marshalSize = Marshal.SizeOf<T>();
        if (unsafeSize != marshalSize)
            throw new InvalidOperationException($"{typeof(T).Name} CPU size mismatch: Unsafe.SizeOf={unsafeSize}, Marshal.SizeOf={marshalSize}.");
    }
}
