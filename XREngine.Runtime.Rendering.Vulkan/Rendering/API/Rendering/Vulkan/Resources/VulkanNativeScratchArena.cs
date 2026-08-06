using System;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retains a typed, managed backing store for short-lived Vulkan ABI arrays.
/// The arena is intended to be owned by one renderer thread or frame slot; its
/// reservations expose spans so native pointers are acquired only at the call
/// boundary.
/// </summary>
internal sealed class VulkanNativeScratchArena<T>
    where T : unmanaged
{
    private T[] _storage;
    private ulong _generation;

    /// <summary>
    /// Creates an arena with reusable storage for at least <paramref name="initialCapacity"/> values.
    /// </summary>
    public VulkanNativeScratchArena(int initialCapacity = 4)
    {
        if (initialCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));

        _storage = initialCapacity == 0 ? Array.Empty<T>() : new T[initialCapacity];
    }

    /// <summary>
    /// Reserves a contiguous span with the requested ABI alignment contract.
    /// A subsequent reservation invalidates every earlier reservation.
    /// </summary>
    public VulkanNativeScratchReservation<T> Reserve(int count, int alignment = 0)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        int elementSize = Unsafe.SizeOf<T>();
        int resolvedAlignment = alignment == 0
            ? Math.Min(IntPtr.Size, GetLargestPowerOfTwoDivisor(elementSize))
            : alignment;
        if (resolvedAlignment <= 0 ||
            (resolvedAlignment & (resolvedAlignment - 1)) != 0 ||
            elementSize % resolvedAlignment != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(alignment),
                "The requested alignment must be a positive power of two that divides the element size.");
        }

        if (_storage.Length < count)
            _storage = new T[GetExpandedCapacity(count)];

        ulong generation = unchecked(++_generation);
        if (generation == 0)
            generation = ++_generation;

        return new VulkanNativeScratchReservation<T>(this, count, generation);
    }

    internal Span<T> GetSpan(int count, ulong generation)
    {
        if (generation == 0 || generation != _generation)
            throw new InvalidOperationException("The Vulkan native scratch reservation has expired.");
        if ((uint)count > (uint)_storage.Length)
            throw new InvalidOperationException("The Vulkan native scratch reservation exceeds its backing store.");

        return _storage.AsSpan(0, count);
    }

    /// <summary>Invalidates outstanding reservations while retaining reusable storage.</summary>
    public void Reset()
    {
        _generation = 0;
        Array.Clear(_storage);
    }

    private int GetExpandedCapacity(int requiredCount)
    {
        int capacity = Math.Max(_storage.Length, 4);
        while (capacity < requiredCount)
        {
            if (capacity > int.MaxValue / 2)
                return requiredCount;

            capacity *= 2;
        }

        return capacity;
    }

    private static int GetLargestPowerOfTwoDivisor(int value)
        => value & -value;
}
