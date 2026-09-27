using System.Collections.Generic;

using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceLifetimeTracker
{
    /// <summary>
    /// Groups every tracked, not-yet-destroyed native resource by object type and
    /// registering owner and returns the largest groups. Owner labels may carry a
    /// per-instance suffix after '#'; collapsing it aggregates per owning code path.
    /// This is a cold diagnostic path: it allocates on request and holds the tracker
    /// lock for one pass over the ledger, so callers must not invoke it per frame.
    /// </summary>
    internal List<VulkanLiveResourceOwnerCount> CaptureLiveResourceOwnerSummary(int top, bool collapseOwnerSuffix)
    {
        Dictionary<(ObjectType Type, string Owner), (int Live, int Pending)> groups = new();
        using (VulkanFrameLockScope.Enter(
                   SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            foreach (VulkanResourceLifetimeRecord resource in ResourceLifetimes.Values)
            {
                if ((resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)
                    continue;

                string owner = resource.Owner ?? string.Empty;
                if (collapseOwnerSuffix)
                {
                    int suffix = owner.IndexOf('#');
                    if (suffix >= 0)
                        owner = owner.Substring(0, suffix);
                }
                (ObjectType Type, string Owner) key = (resource.Key.Type, owner);
                groups.TryGetValue(key, out (int Live, int Pending) counts);
                counts.Live++;
                if ((resource.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)
                    counts.Pending++;
                groups[key] = counts;
            }
        }

        List<VulkanLiveResourceOwnerCount> rows = new(groups.Count);
        foreach (KeyValuePair<(ObjectType Type, string Owner), (int Live, int Pending)> pair in groups)
            rows.Add(new VulkanLiveResourceOwnerCount(pair.Key.Type.ToString(), pair.Key.Owner, pair.Value.Live, pair.Value.Pending));
        rows.Sort(static (left, right) => right.Live.CompareTo(left.Live));
        if (top > 0 && rows.Count > top)
            rows.RemoveRange(top, rows.Count - top);
        return rows;
    }
}
