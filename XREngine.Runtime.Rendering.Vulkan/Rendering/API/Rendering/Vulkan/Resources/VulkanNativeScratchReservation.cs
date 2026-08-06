using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// A generation-validated span reservation from a <see cref="VulkanNativeScratchArena{T}"/>.
/// </summary>
internal readonly ref struct VulkanNativeScratchReservation<T>
    where T : unmanaged
{
    private readonly VulkanNativeScratchArena<T> _owner;
    private readonly int _count;
    private readonly ulong _generation;

    internal VulkanNativeScratchReservation(VulkanNativeScratchArena<T> owner, int count, ulong generation)
    {
        _owner = owner;
        _count = count;
        _generation = generation;
    }

    /// <summary>Gets the reserved, generation-validated storage.</summary>
    public Span<T> Span => _owner.GetSpan(_count, _generation);
}
