using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed record VulkanPublishedDescriptorSetSnapshot(
    ulong Generation,
    ulong ImagePayloadGeneration,
    ulong DescriptorSetLifetimeGeneration,
    VulkanResourceLifetimeKey[] References,
    VulkanPublishedDescriptorImageReference[] ImageReferences,
    uint[] ReflectedImageBindings,
    bool HasReflection,
    EVulkanDescriptorNativePublicationState NativePublicationState)
{
    private VulkanRecordedDescriptorResourceIdentityBuffer _recordedResources;
    private int _recordedResourcesReady;

    internal bool IsNativePublicationKnown =>
        NativePublicationState ==
            EVulkanDescriptorNativePublicationState.Known;

    /// <summary>
    /// Materializes the exact recording identity only when a command artifact
    /// consumes this immutable publication. Descriptor construction may publish
    /// several intermediate snapshots, so eager identity arrays turn setup into
    /// avoidable hot-path allocation churn.
    /// </summary>
    internal VulkanRecordedDescriptorResourceIdentityBuffer
        GetOrCreateRecordedResources(VulkanResourceLifetimeTracker tracker)
    {
        if (Volatile.Read(ref _recordedResourcesReady) != 0)
            return _recordedResources;

        using (VulkanFrameLockScope.Enter(
                   this,
                   EVulkanFrameWaitReason.DescriptorPublicationLock))
        {
            if (_recordedResourcesReady != 0)
                return _recordedResources;

            using (VulkanFrameLockScope.Enter(
                       tracker.SyncRoot,
                       EVulkanFrameWaitReason.ResourceLifetimeLock))
                _recordedResources = CaptureRecordedResourcesNoLock(tracker);
            Volatile.Write(ref _recordedResourcesReady, 1);
            return _recordedResources;
        }
    }

    private VulkanRecordedDescriptorResourceIdentityBuffer
        CaptureRecordedResourcesNoLock(VulkanResourceLifetimeTracker tracker)
    {
        if (!IsNativePublicationKnown)
            return default;

        int required = References.Length + ImageReferences.Length;
        for (int index = 0; index < References.Length; index++)
        {
            VulkanResourceLifetimeKey key = References[index];
            if (key.Type == ObjectType.ImageView &&
                tracker.ImageViewBackingImages.ContainsKey(key.Handle))
            {
                required++;
            }
        }

        VulkanRecordedDescriptorResourceIdentityBuffer result = default;
        result.Initialize(required);
        if (!result.IsComplete)
            return result;

        int resourceIndex = 0;
        for (int index = 0; index < References.Length; index++)
        {
            VulkanResourceLifetimeKey key = References[index];
            if (!tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? resource))
            {
                result.Invalidate();
                return result;
            }

            result.Set(
                resourceIndex++,
                new VulkanRecordedDescriptorResourceIdentity(
                    key.Type,
                    key.Handle,
                    resource.Generation,
                    ImageLayout.Undefined));
            if (key.Type != ObjectType.ImageView ||
                !tracker.ImageViewBackingImages.TryGetValue(
                    key.Handle,
                    out ulong backingImageHandle))
            {
                continue;
            }

            VulkanResourceLifetimeKey backingKey = new(
                ObjectType.Image,
                backingImageHandle);
            if (backingImageHandle == 0UL ||
                !tracker.ResourceLifetimes.TryGetValue(
                    backingKey,
                    out VulkanResourceLifetimeRecord? backingImage))
            {
                result.Invalidate();
                return result;
            }

            result.Set(
                resourceIndex++,
                new VulkanRecordedDescriptorResourceIdentity(
                    ObjectType.Image,
                    backingImageHandle,
                    backingImage.Generation,
                    ImageLayout.Undefined));
        }

        for (int index = 0; index < ImageReferences.Length; index++)
        {
            VulkanDescriptorImageReference image =
                ImageReferences[index].Reference;
            ulong viewHandle = image.View.Handle;
            VulkanResourceLifetimeKey viewKey = new(
                ObjectType.ImageView,
                viewHandle);
            if (!tracker.ResourceLifetimes.TryGetValue(
                    viewKey,
                    out VulkanResourceLifetimeRecord? view))
            {
                result.Invalidate();
                return result;
            }

            result.Set(
                resourceIndex++,
                new VulkanRecordedDescriptorResourceIdentity(
                    ObjectType.ImageView,
                    viewHandle,
                    view.Generation,
                    image.Layout));
        }

        return result;
    }
}
