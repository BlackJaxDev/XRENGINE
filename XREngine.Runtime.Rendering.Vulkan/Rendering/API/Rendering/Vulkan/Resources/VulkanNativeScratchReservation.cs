using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>A generation and thread-validated native scratch lease.</summary>
internal readonly ref struct VulkanNativeScratchReservation<T>
    where T : unmanaged
{
    private readonly VulkanNativeScratchArena<T> _owner;
    private readonly int _count;
    private readonly ulong _generation;
    private readonly int _threadId;
    private readonly int _alignment;

    internal VulkanNativeScratchReservation(VulkanNativeScratchArena<T> owner, int count, ulong generation, int threadId, int alignment)
    {
        _owner = owner;
        _count = count;
        _generation = generation;
        _threadId = threadId;
        _alignment = alignment;
    }

    public Span<T> Span => _owner.GetSpan(_count, _generation, _threadId, _alignment);
    public void Dispose() => _owner.Release(_generation, _threadId);
}
