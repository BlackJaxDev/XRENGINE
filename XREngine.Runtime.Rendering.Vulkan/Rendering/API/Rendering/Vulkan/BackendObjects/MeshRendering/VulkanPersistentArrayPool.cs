using System;
using System.Collections.Generic;
using System.Numerics;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owner-local persistent array storage for prepared draw payloads. It avoids
/// process-wide pool contention and grows only when an owner observes a new
/// capacity class.
/// </summary>
internal sealed class VulkanPersistentArrayPool<T>
{
    private const int MinimumCapacity = 4;
    private const int MaximumRetainedPerCapacity = 32;
    private readonly object _sync = new();
    private readonly Dictionary<int, Stack<T[]>> _availableByCapacity = [];

    public T[] Rent(int minimumLength)
    {
        int capacity = ResolveCapacity(minimumLength);
        lock (_sync)
        {
            if (_availableByCapacity.TryGetValue(capacity, out Stack<T[]>? available) &&
                available.Count > 0)
            {
                return available.Pop();
            }
        }

        return new T[capacity];
    }

    public void Return(T[]? buffer, bool clear = false)
    {
        if (buffer is null || buffer.Length == 0)
            return;

        if (clear || RuntimeHelpersEx<T>.ContainsReferences)
            Array.Clear(buffer);

        lock (_sync)
        {
            if (!_availableByCapacity.TryGetValue(buffer.Length, out Stack<T[]>? available))
            {
                available = new Stack<T[]>(4);
                _availableByCapacity.Add(buffer.Length, available);
            }

            if (available.Count < MaximumRetainedPerCapacity)
                available.Push(buffer);
        }
    }

    private static int ResolveCapacity(int minimumLength)
    {
        uint requested = (uint)Math.Max(minimumLength, MinimumCapacity);
        uint rounded = BitOperations.RoundUpToPowerOf2(requested);
        if (rounded == 0 || rounded > int.MaxValue)
            return minimumLength;

        return (int)rounded;
    }

    private static class RuntimeHelpersEx<TValue>
    {
        public static readonly bool ContainsReferences =
            System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<TValue>();
    }
}
