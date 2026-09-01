using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanTextureUploadService
{
    /// <summary>
    /// Reserves one whole already-signaled batch for completion. Native handles
    /// remain batch-owned when the budget is exhausted, so no child can be
    /// partially retired or published ahead of its siblings.
    /// </summary>
    private bool TryReserveCompletedBatchBudget(
        VulkanSubmittedImportedTextureUploadBatch batch,
        VulkanTextureUploadManifest? requiredManifest)
    {
        ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        if (_transferCompletionBudgetFrameId != frameId)
        {
            _transferCompletionBudgetFrameId = frameId;
            _transferRetirementItemsThisFrame = 0;
            _transferRetirementBytesThisFrame = 0;
            _transferPublicationItemsThisFrame = 0;
            _transferPublicationBytesThisFrame = 0;
        }

        int publicationItems = 0;
        for (int index = 0; index < batch.Uploads.Length; index++)
        {
            VulkanImportedTexturePendingUpload upload = batch.Uploads[index];
            if (upload.CurrentChunkIsFinal && upload.ShouldPublish())
                publicationItems++;
        }

        // This is descriptor payload work, not image allocation size. It
        // corresponds to one VkDescriptorImageInfo per final publication.
        long publicationBytes = (long)publicationItems * Unsafe.SizeOf<DescriptorImageInfo>();

        // A required manifest is owned by an accepted PresentNow transaction.
        // Its exact foreground barrier cannot defer a signaled batch to a later
        // render frame: that would invalidate and rebuild the same accepted plan
        // indefinitely. Batches are atomic, so reserve the entire batch even when
        // it also contains background siblings. Ordinary streaming completion
        // remains governed by the limits below.
        if (requiredManifest is not null)
        {
            _transferRetirementItemsThisFrame += batch.Uploads.Length;
            _transferRetirementBytesThisFrame += batch.BytesInFlight;
            _transferPublicationItemsThisFrame += publicationItems;
            _transferPublicationBytesThisFrame += publicationBytes;
            return true;
        }

        bool fits =
            _transferRetirementItemsThisFrame + batch.Uploads.Length <= MaxTransferCompletionItemsPerFrame &&
            _transferRetirementBytesThisFrame + batch.BytesInFlight <= MaxTransferRetirementBytesPerFrame &&
            _transferPublicationItemsThisFrame + publicationItems <= MaxTransferCompletionItemsPerFrame &&
            _transferPublicationBytesThisFrame + publicationBytes <= MaxTransferRetirementBytesPerFrame;

        if (!fits)
        {
            Interlocked.Increment(ref s_transferRetirementBudgetDeferrals);
            if (publicationItems > 0)
                Interlocked.Increment(ref s_transferPublicationBudgetDeferrals);

            return false;
        }

        _transferRetirementItemsThisFrame += batch.Uploads.Length;
        _transferRetirementBytesThisFrame += batch.BytesInFlight;
        _transferPublicationItemsThisFrame += publicationItems;
        _transferPublicationBytesThisFrame += publicationBytes;
        return true;
    }

    internal static void RecordImportedTextureNativeAllocationCpu(double milliseconds)
    {
        Interlocked.Increment(ref s_nativeAllocationCpuCount);
        Volatile.Write(ref s_lastNativeAllocationCpuMilliseconds, milliseconds);
    }

    internal static void RecordImportedTextureStagingCopyCpu(double milliseconds)
    {
        Interlocked.Increment(ref s_stagingCopyCpuCount);
        Volatile.Write(ref s_lastStagingCopyCpuMilliseconds, milliseconds);
    }

    private static void RecordImportedTextureTransferRecordCpu(double milliseconds)
    {
        Interlocked.Increment(ref s_transferRecordCpuCount);
        Volatile.Write(ref s_lastTransferRecordCpuMilliseconds, milliseconds);
    }

    internal static void RecordImportedTextureTransferGpu(double milliseconds)
    {
        Interlocked.Increment(ref s_transferGpuTimingSamples);
        Volatile.Write(ref s_lastTransferGpuMilliseconds, milliseconds);
    }

    internal static void RecordImportedTextureTransferGpuUnavailable()
        => Interlocked.Increment(ref s_transferGpuTimingUnavailable);
}
