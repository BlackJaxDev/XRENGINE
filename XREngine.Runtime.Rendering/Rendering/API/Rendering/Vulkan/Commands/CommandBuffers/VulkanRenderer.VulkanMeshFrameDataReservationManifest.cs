namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Reusable, allocation-free reservation manifest for one complete command stream.
    /// It is populated and materialized before Vulkan recording starts, then sealed until
    /// the command buffer has finished recording.
    /// </summary>
    internal sealed class VulkanMeshFrameDataReservationManifest
    {
        private readonly Dictionary<VkMeshRenderer, int> _requiredDrawSlots =
            new(ReferenceEqualityComparer.Instance);

        public ulong Generation { get; private set; }
        public ulong ReservedBytesAtSeal { get; private set; }
        public bool IsSealed { get; private set; }
        public int RendererCount => _requiredDrawSlots.Count;

        public void Begin(ulong generation, int capacityHint)
        {
            _requiredDrawSlots.Clear();
            _requiredDrawSlots.EnsureCapacity(Math.Max(capacityHint, 1));
            Generation = generation;
            ReservedBytesAtSeal = 0;
            IsSealed = false;
        }

        public bool TryReserve(VkMeshRenderer renderer, int requiredDrawSlots)
        {
            if (IsSealed || renderer is null || requiredDrawSlots <= 0)
                return false;

            if (_requiredDrawSlots.TryGetValue(renderer, out int existing))
                _requiredDrawSlots[renderer] = Math.Max(existing, requiredDrawSlots);
            else
                _requiredDrawSlots.Add(renderer, requiredDrawSlots);
            return true;
        }

        public bool TryGetRequiredDrawSlots(VkMeshRenderer renderer, out int requiredDrawSlots)
            => _requiredDrawSlots.TryGetValue(renderer, out requiredDrawSlots);

        public bool TrySeal(ulong generation, ulong reservedBytes)
        {
            if (IsSealed || generation != Generation)
                return false;

            ReservedBytesAtSeal = reservedBytes;
            IsSealed = true;
            return true;
        }

        public bool ContainsSealedDraw(VkMeshRenderer renderer, int drawSlot, ulong generation)
            => IsSealed &&
               generation == Generation &&
               drawSlot >= 0 &&
               _requiredDrawSlots.TryGetValue(renderer, out int requiredDrawSlots) &&
               drawSlot < requiredDrawSlots;

        public void End()
        {
            IsSealed = false;
            Generation = 0;
            ReservedBytesAtSeal = 0;
        }
    }
}
