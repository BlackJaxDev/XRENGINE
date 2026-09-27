using System.Collections.Generic;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	/// <summary>
	/// Age after which an allocation variant that has not been activated is treated
	/// as superseded. Every descriptor bind and every validated recorded-command
	/// reuse activates its allocation, so a variant idle this long is not referenced
	/// by any frame still in flight; its sets are still released through ticketed
	/// lifetime retirement rather than freed immediately.
	/// </summary>
	private static readonly long StaleDescriptorAllocationVariantTicks =
		Stopwatch.Frequency * 5;

	/// <summary>
	/// Retires this renderer's allocation variants that have been idle longer than
	/// <see cref="StaleDescriptorAllocationVariantTicks"/>. Called only when a new
	/// variant is published, so the scan cost is bounded by publication events,
	/// never per frame. New variants arrive whenever material-table closures,
	/// textures, local buffers or draw-slot identity republish; without this bound
	/// every such republication retained a full set of descriptor sets for the
	/// renderer's lifetime.
	/// </summary>
	private void RetireSupersededDescriptorAllocationVariants(DescriptorAllocation publishedAllocation)
	{
		long now = Stopwatch.GetTimestamp();
		List<DescriptorAllocationKey>? staleKeys = null;
		foreach (KeyValuePair<DescriptorAllocationKey, DescriptorAllocation> pair in _descriptorAllocations)
		{
			DescriptorAllocation candidate = pair.Value;
			if (ReferenceEquals(candidate, publishedAllocation) ||
				candidate.LastUsedTimestamp == 0 ||
				now - candidate.LastUsedTimestamp < StaleDescriptorAllocationVariantTicks)
			{
				continue;
			}

			(staleKeys ??= []).Add(pair.Key);
		}

		if (staleKeys is null)
			return;

		for (int index = 0; index < staleKeys.Count; index++)
		{
			DescriptorAllocationKey key = staleKeys[index];
			if (!_descriptorAllocations.Remove(key, out DescriptorAllocation? stale))
				continue;

			bool wasActive = ReferenceEquals(_activeDescriptorAllocation, stale);
			RemoveDescriptorDrawSlotLookupEntries(stale);
			ReleaseDescriptorAllocationReference(key, stale);
			if (wasActive)
				ClearActiveDescriptorAllocation();
			RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanMeshDescriptorSupersededVariantRetirement();
		}
	}
}
