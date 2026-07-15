using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Publishes the mesh frame-data capacity shared by every command stream in one
    /// engine render frame. The first recorder seals the generation. A stream that
    /// introduces a new family or larger renderer requirement after that boundary is
    /// rejected and teaches the next frame's generation instead of mutating state
    /// captured by an already recorded or submitted output.
    /// </summary>
    internal sealed class VulkanFrameWideMeshFrameDataReservationManifest
    {
        // Draw counts within an output family vary slightly with bounded work such as
        // asynchronous occlusion probes. Publishing the exact first-observed count would
        // relocate that family as soon as a later frame schedules one more probe, leaving
        // already-recorded command buffers pointing at the old draw-slot base. Reserve a
        // modest power-of-two block on first publication so the base remains stable across
        // normal per-frame variation without turning every renderer into a large sparse slab.
        private const int MinimumFamilySlotCapacity = 32;

        private readonly record struct FamilyAllocation(int BaseSlot, int SlotCount);

        private readonly object _sync = new();
        private readonly Dictionary<VkMeshRenderer, int> _publishedDrawSlots =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VulkanMeshFrameDataFamilyKey, FamilyAllocation> _publishedFamilies = [];
        private readonly Dictionary<VulkanMeshFrameDataFamilyKey, int> _pendingFamilyStrides = [];
        private readonly Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> _pendingRendererFamilyDrawSlots =
            new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
        private ulong _frameId = ulong.MaxValue;
        private ulong _generation;
        private long _publicationCount;
        private long _lateRegistrationCount;
        private readonly Dictionary<int, int> _nextFamilyBaseSlotByFrameDataSlot = [];
        private bool _isSealed;

        public ulong FrameId
        {
            get
            {
                lock (_sync)
                    return _frameId;
            }
        }

        public ulong Generation
        {
            get
            {
                lock (_sync)
                    return _generation;
            }
        }

        public bool IsSealed
        {
            get
            {
                lock (_sync)
                    return _isSealed;
            }
        }

        public int PublishedRendererCount
        {
            get
            {
                lock (_sync)
                    return _publishedDrawSlots.Count;
            }
        }

        public int PublishedFamilyCount
        {
            get
            {
                lock (_sync)
                    return _publishedFamilies.Count;
            }
        }

        public long PublicationCount
        {
            get
            {
                lock (_sync)
                    return _publicationCount;
            }
        }

        public long LateRegistrationCount
        {
            get
            {
                lock (_sync)
                    return _lateRegistrationCount;
            }
        }

        public bool TryRegister(
            ulong frameId,
            Dictionary<VkMeshRenderer, int> requiredDrawSlots,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> rendererFamilyDrawSlots,
            Dictionary<VulkanMeshFrameDataFamilyKey, int> requiredFamilyStrides,
            Dictionary<VulkanMeshFrameDataFamilyKey, int> resolvedFamilyBases,
            bool sealAfterRegister,
            out ulong generation,
            out string reason)
        {
            lock (_sync)
            {
                if (_frameId != frameId)
                    BeginFrame(frameId);

                requiredDrawSlots.Clear();
                resolvedFamilyBases.Clear();

                if (_isSealed)
                {
                    string? firstFailure = null;
                    foreach (KeyValuePair<VulkanMeshFrameDataFamilyKey, int> familyRequirement in requiredFamilyStrides)
                    {
                        VulkanMeshFrameDataFamilyKey family = familyRequirement.Key;
                        int requiredStride = Math.Max(familyRequirement.Value, 1);
                        if (_publishedFamilies.TryGetValue(family, out FamilyAllocation allocation))
                        {
                            resolvedFamilyBases[family] = allocation.BaseSlot;
                            if (allocation.SlotCount >= requiredStride)
                                continue;
                        }

                        QueuePendingFamily(family, requiredStride);
                        firstFailure ??=
                            $"output family {family} requires a {requiredStride}-slot range after frame-wide manifest generation {_generation} was sealed for render frame {frameId}";
                    }

                    foreach (KeyValuePair<VulkanMeshFrameDataRendererFamilyKey, int> requirement in rendererFamilyDrawSlots)
                    {
                        VulkanMeshFrameDataRendererFamilyKey key = requirement.Key;
                        if (!_publishedFamilies.TryGetValue(key.Family, out FamilyAllocation allocation) ||
                            allocation.SlotCount < requirement.Value)
                        {
                            QueuePendingRendererFamily(key, requirement.Value);
                            continue;
                        }

                        int requiredSlots = checked(allocation.BaseSlot + allocation.SlotCount);
                        AccumulateRendererRequirement(requiredDrawSlots, key.Renderer, requiredSlots);
                        if (_publishedDrawSlots.TryGetValue(key.Renderer, out int published) &&
                            published >= requiredSlots)
                        {
                            continue;
                        }

                        QueuePendingRendererFamily(key, requirement.Value);
                        firstFailure ??=
                            $"renderer '{key.Renderer.Mesh?.Name ?? "<unnamed mesh>"}' requires {requiredSlots} draw slots " +
                            $"after frame-wide manifest generation {_generation} was sealed with {published} for render frame {frameId}";
                    }

                    generation = _generation;
                    reason = firstFailure ?? string.Empty;
                    if (firstFailure is not null)
                        _lateRegistrationCount++;
                    return firstFailure is null;
                }

                bool changed = false;
                foreach (KeyValuePair<VulkanMeshFrameDataFamilyKey, int> familyRequirement in requiredFamilyStrides)
                    changed |= PublishFamily(familyRequirement.Key, familyRequirement.Value);

                foreach (KeyValuePair<VulkanMeshFrameDataFamilyKey, int> familyRequirement in requiredFamilyStrides)
                {
                    FamilyAllocation allocation = _publishedFamilies[familyRequirement.Key];
                    resolvedFamilyBases[familyRequirement.Key] = allocation.BaseSlot;
                }

                foreach (KeyValuePair<VulkanMeshFrameDataRendererFamilyKey, int> requirement in rendererFamilyDrawSlots)
                {
                    FamilyAllocation allocation = _publishedFamilies[requirement.Key.Family];
                    int absoluteRequiredSlots = checked(allocation.BaseSlot + allocation.SlotCount);
                    AccumulateRendererRequirement(
                        requiredDrawSlots,
                        requirement.Key.Renderer,
                        absoluteRequiredSlots);
                }

                foreach (KeyValuePair<VkMeshRenderer, int> requirement in requiredDrawSlots)
                    changed |= PublishRendererRequirement(requirement.Key, requirement.Value);

                if (changed || _generation == 0)
                {
                    _generation++;
                    _publicationCount++;
                }

                if (sealAfterRegister)
                    _isSealed = true;

                generation = _generation;
                reason = string.Empty;
                return true;
            }
        }

        public void RemoveRenderer(VkMeshRenderer renderer)
        {
            lock (_sync)
            {
                _publishedDrawSlots.Remove(renderer);
                if (_pendingRendererFamilyDrawSlots.Count == 0)
                    return;

                List<VulkanMeshFrameDataRendererFamilyKey> removed = [];
                foreach (VulkanMeshFrameDataRendererFamilyKey key in _pendingRendererFamilyDrawSlots.Keys)
                {
                    if (ReferenceEquals(key.Renderer, renderer))
                        removed.Add(key);
                }
                foreach (VulkanMeshFrameDataRendererFamilyKey key in removed)
                    _pendingRendererFamilyDrawSlots.Remove(key);
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _publishedDrawSlots.Clear();
                _publishedFamilies.Clear();
                _pendingFamilyStrides.Clear();
                _pendingRendererFamilyDrawSlots.Clear();
                _frameId = ulong.MaxValue;
                _generation = 0;
                _publicationCount = 0;
                _lateRegistrationCount = 0;
                _nextFamilyBaseSlotByFrameDataSlot.Clear();
                _isSealed = false;
            }
        }

        private void BeginFrame(ulong frameId)
        {
            _frameId = frameId;
            _isSealed = false;
            bool changed = false;

            foreach (KeyValuePair<VulkanMeshFrameDataFamilyKey, int> family in _pendingFamilyStrides)
                changed |= PublishFamily(family.Key, family.Value);
            _pendingFamilyStrides.Clear();

            foreach (KeyValuePair<VulkanMeshFrameDataRendererFamilyKey, int> requirement in _pendingRendererFamilyDrawSlots)
            {
                if (!_publishedFamilies.TryGetValue(requirement.Key.Family, out FamilyAllocation allocation))
                    continue;
                int requiredSlots = checked(allocation.BaseSlot + requirement.Value);
                changed |= PublishRendererRequirement(requirement.Key.Renderer, requiredSlots);
            }
            _pendingRendererFamilyDrawSlots.Clear();

            if (changed || _generation == 0)
            {
                _generation++;
                _publicationCount++;
            }
        }

        private bool PublishFamily(VulkanMeshFrameDataFamilyKey family, int requiredStride)
        {
            requiredStride = Math.Max(requiredStride, 1);
            if (_publishedFamilies.TryGetValue(family, out FamilyAllocation published) &&
                published.SlotCount >= requiredStride)
            {
                return false;
            }

            int publishedCapacity = ResolveFamilySlotCapacity(requiredStride);
            _nextFamilyBaseSlotByFrameDataSlot.TryGetValue(family.FrameDataSlot, out int baseSlot);
            _nextFamilyBaseSlotByFrameDataSlot[family.FrameDataSlot] = checked(baseSlot + publishedCapacity);
            _publishedFamilies[family] = new FamilyAllocation(baseSlot, publishedCapacity);
            return true;
        }

        private static int ResolveFamilySlotCapacity(int requiredStride)
        {
            int capacity = MinimumFamilySlotCapacity;
            while (capacity < requiredStride)
                capacity = checked(capacity << 1);
            return capacity;
        }

        private bool PublishRendererRequirement(VkMeshRenderer renderer, int requiredDrawSlots)
        {
            requiredDrawSlots = Math.Max(requiredDrawSlots, 1);
            if (_publishedDrawSlots.TryGetValue(renderer, out int published) &&
                published >= requiredDrawSlots)
            {
                return false;
            }

            _publishedDrawSlots[renderer] = requiredDrawSlots;
            renderer.EnsureUniformDrawSlotCapacity(requiredDrawSlots);
            return true;
        }

        private void QueuePendingFamily(VulkanMeshFrameDataFamilyKey family, int requiredStride)
        {
            if (_pendingFamilyStrides.TryGetValue(family, out int pending) && pending >= requiredStride)
                return;

            _pendingFamilyStrides[family] = Math.Max(requiredStride, 1);
        }

        private void QueuePendingRendererFamily(
            VulkanMeshFrameDataRendererFamilyKey key,
            int requiredDrawSlots)
        {
            if (_pendingRendererFamilyDrawSlots.TryGetValue(key, out int pending) && pending >= requiredDrawSlots)
                return;

            _pendingRendererFamilyDrawSlots[key] = Math.Max(requiredDrawSlots, 1);
        }

        private static void AccumulateRendererRequirement(
            Dictionary<VkMeshRenderer, int> requirements,
            VkMeshRenderer renderer,
            int requiredDrawSlots)
        {
            if (requirements.TryGetValue(renderer, out int existing))
                requirements[renderer] = Math.Max(existing, requiredDrawSlots);
            else
                requirements.Add(renderer, requiredDrawSlots);
        }
    }
}
