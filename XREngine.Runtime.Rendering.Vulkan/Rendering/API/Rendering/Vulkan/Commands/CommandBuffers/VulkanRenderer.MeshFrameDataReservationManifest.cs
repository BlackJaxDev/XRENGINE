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
        // already-recorded command buffers pointing at the old draw-slot base. This floor is
        // applied per renderer, not once per output family: keeping it small prevents a dense
        // shadow refresh from reserving 32 slots for each mostly single-draw mesh renderer.
        private const int MinimumFamilySlotCapacity = 4;

        private readonly record struct FamilyAllocation(int BaseSlot, int SlotCount);

        private readonly object _sync = new();
        private readonly Dictionary<VkMeshRenderer, int> _publishedDrawSlots =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VulkanMeshFrameDataRendererFamilyKey, FamilyAllocation> _publishedRendererFamilies =
            new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
        private readonly Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> _pendingRendererFamilyDrawSlots =
            new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
        private ulong _frameId = ulong.MaxValue;
        private ulong _generation;
        private long _publicationCount;
        private long _lateRegistrationCount;
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
                    return _publishedRendererFamilies.Count;
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
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> resolvedFamilyBases,
            bool sealAfterRegister,
            out ulong generation,
            out string reason)
            => TryRegister(
                frameId,
                requiredDrawSlots,
                rendererFamilyDrawSlots,
                requiredFamilyStrides,
                resolvedFamilyBases,
                sealAfterRegister,
                out generation,
                out _,
                out reason);

        /// <summary>
        /// Publishes the current frame's draw-slot requirements and reports whether an existing
        /// family had to move. New families append safely; only a moved existing family invalidates
        /// cached command buffers with baked dynamic offsets.
        /// </summary>
        public bool TryRegister(
            ulong frameId,
            Dictionary<VkMeshRenderer, int> requiredDrawSlots,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> rendererFamilyDrawSlots,
            Dictionary<VulkanMeshFrameDataFamilyKey, int> requiredFamilyStrides,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> resolvedFamilyBases,
            bool sealAfterRegister,
            out ulong generation,
            out bool existingFamilyBaseChanged,
            out string reason)
        {
            lock (_sync)
            {
                existingFamilyBaseChanged = false;
                if (_frameId != frameId)
                    existingFamilyBaseChanged = BeginFrame(frameId);

                requiredDrawSlots.Clear();
                resolvedFamilyBases.Clear();

                if (_isSealed)
                {
                    string? firstFailure = null;
                    foreach (KeyValuePair<VulkanMeshFrameDataRendererFamilyKey, int> requirement in rendererFamilyDrawSlots)
                    {
                        VulkanMeshFrameDataRendererFamilyKey key = requirement.Key;
                        int requiredStride = requiredFamilyStrides.TryGetValue(key.Family, out int familyStride)
                            ? Math.Max(familyStride, requirement.Value)
                            : Math.Max(requirement.Value, 1);
                        if (!_publishedRendererFamilies.TryGetValue(key, out FamilyAllocation allocation) ||
                            allocation.SlotCount < requiredStride)
                        {
                            QueuePendingRendererFamily(key, requiredStride);
                            firstFailure ??=
                                $"renderer '{key.Renderer.Data?.Parent?.Mesh?.Name ?? "<unnamed mesh>"}' output family {key.Family} " +
                                $"requires a {requiredStride}-slot range after frame-wide manifest generation {_generation} was sealed for render frame {frameId}";
                            continue;
                        }

                        resolvedFamilyBases[key] = allocation.BaseSlot;
                        int requiredSlots = checked(allocation.BaseSlot + allocation.SlotCount);
                        AccumulateRendererRequirement(requiredDrawSlots, key.Renderer, requiredSlots);
                        if (_publishedDrawSlots.TryGetValue(key.Renderer, out int published) &&
                            published >= requiredSlots)
                        {
                            continue;
                        }

                        QueuePendingRendererFamily(key, requiredStride);
                        firstFailure ??=
                            $"renderer '{key.Renderer.Data?.Parent?.Mesh?.Name ?? "<unnamed mesh>"}' requires {requiredSlots} draw slots " +
                            $"after frame-wide manifest generation {_generation} was sealed with {published} for render frame {frameId}";
                    }

                    generation = _generation;
                    reason = firstFailure ?? string.Empty;
                    if (firstFailure is not null)
                        _lateRegistrationCount++;
                    return firstFailure is null;
                }

                bool changed = false;
                foreach (KeyValuePair<VulkanMeshFrameDataRendererFamilyKey, int> requirement in rendererFamilyDrawSlots)
                {
                    int requiredStride = requiredFamilyStrides.TryGetValue(requirement.Key.Family, out int familyStride)
                        ? Math.Max(familyStride, requirement.Value)
                        : Math.Max(requirement.Value, 1);
                    changed |= PublishRendererFamily(
                        requirement.Key,
                        requiredStride,
                        out bool familyBaseChanged);
                    existingFamilyBaseChanged |= familyBaseChanged;
                }

                foreach (KeyValuePair<VulkanMeshFrameDataRendererFamilyKey, int> requirement in rendererFamilyDrawSlots)
                {
                    FamilyAllocation allocation = _publishedRendererFamilies[requirement.Key];
                    resolvedFamilyBases[requirement.Key] = allocation.BaseSlot;
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

                List<VulkanMeshFrameDataRendererFamilyKey> removed = [];
                foreach (VulkanMeshFrameDataRendererFamilyKey key in _publishedRendererFamilies.Keys)
                {
                    if (ReferenceEquals(key.Renderer, renderer))
                        removed.Add(key);
                }
                foreach (VulkanMeshFrameDataRendererFamilyKey key in removed)
                    _publishedRendererFamilies.Remove(key);

                removed.Clear();
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
                _publishedRendererFamilies.Clear();
                _pendingRendererFamilyDrawSlots.Clear();
                _frameId = ulong.MaxValue;
                _generation = 0;
                _publicationCount = 0;
                _lateRegistrationCount = 0;
                _isSealed = false;
            }
        }

        private bool BeginFrame(ulong frameId)
        {
            _frameId = frameId;
            _isSealed = false;
            bool changed = false;
            bool existingFamilyBaseChanged = false;

            foreach (KeyValuePair<VulkanMeshFrameDataRendererFamilyKey, int> requirement in _pendingRendererFamilyDrawSlots)
            {
                changed |= PublishRendererFamily(
                    requirement.Key,
                    requirement.Value,
                    out bool familyBaseChanged);
                existingFamilyBaseChanged |= familyBaseChanged;
                FamilyAllocation allocation = _publishedRendererFamilies[requirement.Key];
                int requiredSlots = checked(allocation.BaseSlot + allocation.SlotCount);
                changed |= PublishRendererRequirement(requirement.Key.Renderer, requiredSlots);
            }
            _pendingRendererFamilyDrawSlots.Clear();

            if (changed || _generation == 0)
            {
                _generation++;
                _publicationCount++;
            }

            return existingFamilyBaseChanged;
        }

        private bool PublishRendererFamily(
            VulkanMeshFrameDataRendererFamilyKey key,
            int requiredStride,
            out bool existingFamilyBaseChanged)
        {
            existingFamilyBaseChanged = false;
            requiredStride = Math.Max(requiredStride, 1);
            if (_publishedRendererFamilies.TryGetValue(key, out FamilyAllocation published) &&
                published.SlotCount >= requiredStride)
            {
                return false;
            }

            int publishedCapacity = ResolveFamilySlotCapacity(requiredStride);
            int baseSlot;
            if (!_publishedRendererFamilies.TryGetValue(key, out published))
            {
                baseSlot = ResolveNextFamilyBaseSlot(key);
            }
            else
            {
                baseSlot = published.BaseSlot;
                int expandedEnd = checked(baseSlot + publishedCapacity);
                if (WouldOverlapAnotherFamily(key, baseSlot, expandedEnd))
                {
                    baseSlot = ResolveNextFamilyBaseSlot(key);
                    existingFamilyBaseChanged = baseSlot != published.BaseSlot;
                }
            }

            _publishedRendererFamilies[key] = new FamilyAllocation(baseSlot, publishedCapacity);
            return true;
        }

        private int ResolveNextFamilyBaseSlot(VulkanMeshFrameDataRendererFamilyKey key)
        {
            int baseSlot = 0;
            foreach (KeyValuePair<VulkanMeshFrameDataRendererFamilyKey, FamilyAllocation> existing in _publishedRendererFamilies)
            {
                if (!ReferenceEquals(existing.Key.Renderer, key.Renderer) ||
                    existing.Key.Family.FrameDataSlot != key.Family.FrameDataSlot)
                {
                    continue;
                }

                baseSlot = Math.Max(baseSlot, checked(existing.Value.BaseSlot + existing.Value.SlotCount));
            }

            return baseSlot;
        }

        private bool WouldOverlapAnotherFamily(
            VulkanMeshFrameDataRendererFamilyKey key,
            int rangeStart,
            int rangeEnd)
        {
            foreach (KeyValuePair<VulkanMeshFrameDataRendererFamilyKey, FamilyAllocation> existing in _publishedRendererFamilies)
            {
                if (existing.Key.Equals(key) ||
                    !ReferenceEquals(existing.Key.Renderer, key.Renderer) ||
                    existing.Key.Family.FrameDataSlot != key.Family.FrameDataSlot)
                {
                    continue;
                }

                int existingStart = existing.Value.BaseSlot;
                int existingEnd = checked(existingStart + existing.Value.SlotCount);
                if (rangeStart < existingEnd && existingStart < rangeEnd)
                    return true;
            }

            return false;
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
