using System.Text;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanDescriptorLifetimeAuthority
{
    /// <summary>
    /// Describes a rejected write using the same locked lifetime snapshot as the
    /// guard, without polling native queues or changing completion evidence.
    /// </summary>
    private static string DescribeDescriptorCompletionNoLock(
        VulkanResourceLifetimeTracker tracker,
        VulkanResourceLifetimeRecord resource)
    {
        Span<ulong> pins = stackalloc ulong[3]
        {
            resource.Pins.LastGraphicsSequence,
            resource.Pins.LastTransferSequence,
            resource.Pins.LastOtherSequence,
        };
        Span<ulong> completed = stackalloc ulong[3]
        {
            tracker.CompletedGraphicsSequence,
            tracker.CompletedTransferSequence,
            tracker.CompletedOtherSequence,
        };
        Span<VulkanLifetimeSubmission> exact = stackalloc VulkanLifetimeSubmission[3];
        Span<VulkanLifetimeSubmission> earliest = stackalloc VulkanLifetimeSubmission[3];
        exact.Clear();
        earliest.Clear();
        for (int i = 0; i < tracker.LifetimeSubmissions.Count; ++i)
        {
            VulkanLifetimeSubmission submission = tracker.LifetimeSubmissions[i];
            int domain = submission.QueueDomain switch
            {
                EVulkanLifetimeQueueDomain.Graphics => 0,
                EVulkanLifetimeQueueDomain.Transfer => 1,
                _ => 2,
            };
            if (pins[domain] <= completed[domain])
                continue;
            if (submission.QueueSequence == pins[domain])
                exact[domain] = submission;
            if (!submission.CompletionObserved && submission.QueueSequence <= pins[domain] &&
                (earliest[domain].QueueSequence == 0 || submission.QueueSequence < earliest[domain].QueueSequence))
                earliest[domain] = submission;
        }

        // Formatting is confined to the already failing path.
        StringBuilder detail = new(1024);
        detail.Append($" Owner='{resource.Owner}' generation={resource.Generation} state={resource.State} lastSubmission={resource.LastSubmissionSerial} lastFrameOp={resource.LastFrameOpContextId}/{resource.LastFrameOpKind}.");
        detail.Append($" References descriptor/template/recorded/queued={resource.Pins.DescriptorReferenceCount}/{resource.Pins.TemplateReferenceCount}/{resource.Pins.RecordedReferenceCount}/{resource.Pins.QueuedReferenceCount}.");
        detail.Append($" Graphics pin/completed/observed/last={pins[0]}/{completed[0]}/{tracker.ObservedGraphicsSequence}/{tracker.LastGraphicsSequence}; Transfer={pins[1]}/{completed[1]}/{tracker.ObservedTransferSequence}/{tracker.LastTransferSequence}; Other={pins[2]}/{completed[2]}/{tracker.ObservedOtherSequence}/{tracker.LastOtherSequence}; retainedSubmissions={tracker.LifetimeSubmissions.Count}.");
        for (int i = 0; i < pins.Length; ++i)
        {
            if (pins[i] <= completed[i])
                continue;
            string domain = i == 0 ? "Graphics" : i == 1 ? "Transfer" : "Other";
            detail.Append($" {domain} exact=[{(exact[i].QueueSequence == 0 ? "not retained" : exact[i].ToString())}] earliestPending=[{(earliest[i].QueueSequence == 0 ? "none" : earliest[i].ToString())}].");
        }
        tracker.ResourceCommandBufferDependencies.TryGetValue(resource.Key, out HashSet<ulong>? dependents);
        foreach ((VulkanResourceLifetimeKey key, VulkanResourceLifetimeRecord commandResource) in tracker.ResourceLifetimes)
        {
            if (key.Type != ObjectType.CommandBuffer ||
                (commandResource.LastSubmissionSerial != resource.LastSubmissionSerial &&
                 (dependents is null || !dependents.Contains(key.Handle))))
                continue;
            detail.Append($" Command=[{key} owner='{commandResource.Owner}' generation={commandResource.Generation} state={commandResource.State} lastSubmission={commandResource.LastSubmissionSerial} pins={commandResource.Pins.LastGraphicsSequence}/{commandResource.Pins.LastTransferSequence}/{commandResource.Pins.LastOtherSequence} reverseDependent={dependents?.Contains(key.Handle) == true}");
            if (tracker.CommandBufferLifetimes.TryGetValue(key.Handle, out VulkanCommandBufferLifetimeRecord? command))
            {
                bool exactDependency = command.Dependencies.TryGetValue(resource.Key, out ulong generation) && generation == resource.Generation;
                bool touched = false;
                foreach (KeyValuePair<VulkanResourceLifetimeKey, ulong> dependency in command.TouchedDependencies)
                    if (dependency.Key == resource.Key && dependency.Value == resource.Generation)
                        touched = true;
                bool sealedDependency = false;
                if (command.SealedSubmissionContract is { } contract)
                    foreach (VulkanSealedResourceDependency dependency in contract.Resources)
                        if (dependency.Key == resource.Key && dependency.Generation == resource.Generation)
                            sealedDependency = true;
                detail.Append($" level={command.Level} recordingGeneration={command.RecordingGeneration} queued={command.QueuedSubmissionCount} exactDependency={exactDependency} touched={touched} sealed={sealedDependency}");
            }
            detail.Append("].");
        }
        return detail.ToString();
    }
}
