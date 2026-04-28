using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Per-frame ring buffer for dynamic uniform buffer offsets.
    /// Allocates aligned sub-ranges from a single large persistently-mapped host-visible buffer.
    /// Callers write uniform data at the returned offset and pass it as a dynamic offset
    /// to <c>vkCmdBindDescriptorSets</c>.
    /// </summary>
    internal sealed class VulkanDynamicUniformRingBuffer
    {
        private readonly VulkanRenderer _renderer;
        private Buffer _buffer;
        private DeviceMemory _memory;
        private readonly ulong _capacity;
        private readonly uint _alignment;
        private ulong _currentOffset;
        private void* _mappedPtr;
        private bool _destroyed;

        internal Buffer VkBuffer => _buffer;
        internal ulong Capacity => _capacity;
        internal ulong CurrentOffset => _currentOffset;
        internal uint Alignment => _alignment;

        /// <summary>
        /// Creates a ring buffer backed by a persistently mapped host-visible coherent Vulkan buffer.
        /// </summary>
        /// <param name="renderer">Owning renderer.</param>
        /// <param name="capacity">Total ring buffer size in bytes (e.g. 4 MB).</param>
        internal VulkanDynamicUniformRingBuffer(VulkanRenderer renderer, ulong capacity)
        {
            _renderer = renderer;
            _capacity = capacity;

            renderer.Api!.GetPhysicalDeviceProperties(renderer._physicalDevice, out PhysicalDeviceProperties props);
            _alignment = (uint)Math.Max(props.Limits.MinUniformBufferOffsetAlignment, 1);

            (_buffer, _memory) = renderer.CreateBufferRaw(
                capacity,
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* mapped;
            if (renderer.Api.MapMemory(renderer.device, _memory, 0, capacity, 0, &mapped) != Result.Success)
            {
                renderer.Api.DestroyBuffer(renderer.device, _buffer, null);
                renderer.Api.FreeMemory(renderer.device, _memory, null);
                throw new InvalidOperationException("Failed to persistently map dynamic UBO ring buffer.");
            }
            _mappedPtr = mapped;
        }

        /// <summary>
        /// Allocates <paramref name="size"/> bytes at the next aligned offset.
        /// Returns the byte offset, or <see cref="ulong.MaxValue"/> if the ring buffer
        /// is exhausted for this frame.
        /// </summary>
        internal ulong Allocate(uint size)
        {
            ulong aligned = AlignUp(_currentOffset, _alignment);
            if (aligned + size > _capacity)
            {
                Engine.Rendering.Stats.RecordVulkanDynamicUniformExhaustion();
                return ulong.MaxValue;
            }
            _currentOffset = aligned + size;
            Engine.Rendering.Stats.RecordVulkanDynamicUniformAllocation(size);
            return aligned;
        }

        /// <summary>
        /// Writes raw bytes at the given offset inside the ring buffer.
        /// The buffer is persistently mapped and host-coherent, so no flush is needed.
        /// </summary>
        internal void Write(ulong offset, ReadOnlySpan<byte> data)
        {
            data.CopyTo(new Span<byte>((byte*)_mappedPtr + offset, data.Length));
        }

        /// <summary>
        /// Writes a blittable struct at the given offset.
        /// </summary>
        internal void Write<T>(ulong offset, in T value) where T : unmanaged
        {
            *(T*)((byte*)_mappedPtr + offset) = value;
        }

        /// <summary>Resets the allocation cursor. Call at the start of each frame.</summary>
        internal void ResetForFrame() => _currentOffset = 0;

        internal void Destroy()
        {
            if (_destroyed) return;
            _destroyed = true;

            if (_mappedPtr != null)
            {
                _renderer.Api!.UnmapMemory(_renderer.device, _memory);
                _mappedPtr = null;
            }

            if (_buffer.Handle != 0)
            {
                _renderer.DestroyBuffer(_buffer, _memory);
                _buffer = default;
                _memory = default;
            }
        }

        private static ulong AlignUp(ulong value, uint alignment)
            => (value + alignment - 1) & ~((ulong)alignment - 1);
    }

    private VulkanDynamicUniformRingBuffer?[] _dynamicUniformRingBuffers = Array.Empty<VulkanDynamicUniformRingBuffer>();

    /// <summary>Default ring buffer capacity per swapchain image: 4 MB.</summary>
    private const ulong DynamicUniformRingBufferCapacity = 4 * 1024 * 1024;

    /// <summary>
    /// Returns the dynamic uniform ring buffer for the given swapchain image, or null if disabled.
    /// </summary>
    internal VulkanDynamicUniformRingBuffer? GetDynamicUniformRingBuffer(uint imageIndex)
    {
        if (_dynamicUniformRingBuffers.Length == 0 || imageIndex >= _dynamicUniformRingBuffers.Length)
            return null;
        return _dynamicUniformRingBuffers[imageIndex];
    }

    private void InitializeDynamicUniformRingBuffers()
    {
        if (!Engine.Rendering.Settings.VulkanRobustnessSettings.DynamicUniformBufferEnabled)
            return;

        int count = swapChainImages?.Length ?? 0;
        if (count == 0) return;

        _dynamicUniformRingBuffers = new VulkanDynamicUniformRingBuffer[count];
        for (int i = 0; i < count; i++)
            _dynamicUniformRingBuffers[i] = new VulkanDynamicUniformRingBuffer(this, DynamicUniformRingBufferCapacity);

        Debug.Vulkan($"[Vulkan] Dynamic uniform ring buffers initialized: {count} x {DynamicUniformRingBufferCapacity / 1024} KB, alignment={_dynamicUniformRingBuffers[0]?.Alignment}");
    }

    private void DestroyDynamicUniformRingBuffers()
    {
        for (int i = 0; i < _dynamicUniformRingBuffers.Length; i++)
        {
            _dynamicUniformRingBuffers[i]?.Destroy();
            _dynamicUniformRingBuffers[i] = null;
        }
        _dynamicUniformRingBuffers = Array.Empty<VulkanDynamicUniformRingBuffer>();
    }

    /// <summary>
    /// Resets the ring buffer for the given swapchain image at frame start.
    /// </summary>
    internal void ResetDynamicUniformRingBuffer(uint imageIndex)
    {
        if (imageIndex < _dynamicUniformRingBuffers.Length)
            _dynamicUniformRingBuffers[imageIndex]?.ResetForFrame();
    }
}
