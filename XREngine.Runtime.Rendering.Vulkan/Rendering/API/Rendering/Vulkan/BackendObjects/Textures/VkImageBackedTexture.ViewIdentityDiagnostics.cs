using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

internal unsafe abstract partial class VkImageBackedTexture<TTexture> where TTexture : XRTexture
{
    private static readonly bool ViewIdentityDiagnosticsEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanRecordingDiag),
            "1",
            StringComparison.Ordinal);
    private static int _viewCreationDiagnosticCount;
    private static int _viewDeletionDiagnosticCount;

    private bool ShouldTraceViewIdentity(ref int count)
    {
        if (!ViewIdentityDiagnosticsEnabled)
            return false;

        if (System.Threading.Volatile.Read(ref count) >= 32)
            return false;

        string? name = Data.Name;
        if (name is not "ForwardPrePassDepthStencil" and not "HistoryDepthStencil")
            return false;

        return System.Threading.Interlocked.Increment(ref count) <= 32;
    }

    private void TraceImageViewCreation(ImageView view, Image image, ulong nativeGeneration)
    {
        if (!ShouldTraceViewIdentity(ref _viewCreationDiagnosticCount))
            return;

        Debug.WriteAuxiliaryLog(
            "vulkan-view-owner-identity.log",
            $"event=create resource='{ResolveLogicalResourceName() ?? Data.Name}' " +
            $"wrapperId={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)} " +
            $"textureId={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Data)} " +
            $"view=0x{view.Handle:X} image=0x{image.Handle:X} nativeGeneration={nativeGeneration} " +
            $"bindingId={_bindingId?.ToString() ?? "<none>"} isRetired={IsRetired} " +
            $"threadId={Environment.CurrentManagedThreadId} descriptorGeneration={DescriptorGeneration}");
    }

    private void TracePrimaryViewDeletion(ImageView primaryView, Image submittedImage, Image currentImage)
    {
        if (!ShouldTraceViewIdentity(ref _viewDeletionDiagnosticCount))
            return;

        VulkanResourceLifetimeTracker tracker = BackendContext.Resources.Lifetime.Tracker;
        ulong registeredGeneration = 0;
        ulong backingImage = 0;
        EVulkanResourceLifetimeState state = default;
        bool hasRecord;
        lock (tracker.SyncRoot)
        {
            hasRecord = tracker.ResourceLifetimes.TryGetValue(
                new VulkanResourceLifetimeKey(ObjectType.ImageView, primaryView.Handle),
                out VulkanResourceLifetimeRecord? record);
            if (hasRecord)
            {
                registeredGeneration = record!.Generation;
                state = record.State;
            }
            tracker.ImageViewBackingImages.TryGetValue(primaryView.Handle, out backingImage);
        }

        Debug.WriteAuxiliaryLog(
            "vulkan-view-owner-identity.log",
            $"event=delete resource='{ResolveLogicalResourceName() ?? Data.Name}' " +
            $"wrapperId={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)} " +
            $"textureId={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Data)} " +
            $"submittedPrimaryView=0x{primaryView.Handle:X} submittedImage=0x{submittedImage.Handle:X} " +
            $"currentImage=0x{currentImage.Handle:X} viewRecord={hasRecord} " +
            $"registeredGeneration={registeredGeneration} backingImage=0x{backingImage:X} state={state} " +
            $"bindingId={_bindingId?.ToString() ?? "<none>"} isRetired={IsRetired} " +
            $"threadId={Environment.CurrentManagedThreadId} descriptorGeneration={DescriptorGeneration}");
    }
}
