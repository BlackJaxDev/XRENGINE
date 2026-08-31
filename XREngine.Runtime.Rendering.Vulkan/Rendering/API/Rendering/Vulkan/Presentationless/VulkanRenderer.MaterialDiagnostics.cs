using XREngine.Rendering.Materials;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal bool TryReadbackMaterialTablePublication(in VulkanExplicitProductionSubmissionReceipt receipt,
        GPUMaterialTablePublication publication, out VulkanMaterialTableDiagnosticSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(publication);
        snapshot = null;
        if (!HasExplicitFrameTarget || !RuntimeEngine.IsRenderThread ||
            !_frameLoop.TryEnterExplicitTextureDiagnostic(in receipt))
            return false;
        try
        {
            if (_resourceRuntime.BackendObjectContext is not { } context ||
                !_resourceRuntime.MaterialTablePreparedMap.TryReadPublication(context, _resourceRuntime.Buffers,
                    publication, out var binding, out byte[] bytes))
                return false;
            snapshot = new(binding.Buffer.Handle, binding.NativeGeneration, binding.Range, binding.RowByteStride,
                binding.TableOwnerId, binding.RowGeneration, binding.DescriptorClosureGeneration, bytes);
            return true;
        }
        finally
        {
            _frameLoop.ExitExplicitTextureDiagnostic();
        }
    }

    internal VulkanMaterialTableDiagnosticCounters GetMaterialTableDiagnostics()
    {
        var counts = _resourceRuntime.MaterialTablePreparedMap.SnapshotCounters();
        var descriptors = _resourceRuntime.Descriptors.SnapshotMaterialDescriptorDiagnostics();
        return new(counts.NativeAllocations, counts.PageWrites, counts.BytesWritten, counts.Reuses,
            counts.GrowthPending, counts.EmergencyWaits, counts.Banks, counts.PendingAllocations,
            descriptors.Writes, descriptors.Retirements, descriptors.Acquires, descriptors.Releases,
            descriptors.LiveSlots, descriptors.LeasedSlots);
    }
}
