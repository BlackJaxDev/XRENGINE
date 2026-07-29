using System.Runtime.CompilerServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Fixed-capacity collision-safe reference identity table used only during
/// scene extraction. Full reference equality is checked after every hash
/// probe; managed object hashes never become GPU identity by themselves.
/// </summary>
public sealed class AdvancedReferenceHandleTable
{
    private readonly object?[] _references;
    private readonly uint[] _generations;
    private int _count;

    public AdvancedReferenceHandleTable(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _references = new object?[NextPowerOfTwo(checked(capacity * 2))];
        _generations = new uint[_references.Length];
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int Count => _count;

    public bool TryGetOrAdd(object reference, out AdvancedGpuHandle handle)
    {
        ArgumentNullException.ThrowIfNull(reference);
        uint mask = checked((uint)_references.Length - 1u);
        uint start = Hash(reference) & mask;
        for (uint probe = 0u; probe < (uint)_references.Length; probe++)
        {
            int slot = checked((int)((start + probe) & mask));
            object? existing = _references[slot];
            if (ReferenceEquals(existing, reference))
            {
                handle = new AdvancedGpuHandle(
                    checked((uint)slot + 1u),
                    _generations[slot]);
                return true;
            }
            if (existing is not null)
                continue;
            if (_count >= Capacity)
                break;

            _references[slot] = reference;
            uint generation = _generations[slot] + 1u;
            if (generation == 0u)
                generation = 1u;
            _generations[slot] = generation;
            _count++;
            handle = new AdvancedGpuHandle(
                checked((uint)slot + 1u),
                generation);
            return true;
        }

        handle = AdvancedGpuHandle.Invalid;
        return false;
    }

    private static uint Hash(object reference)
    {
        uint value = unchecked((uint)RuntimeHelpers.GetHashCode(reference));
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        return value;
    }

    private static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
            result = checked(result << 1);
        return result;
    }
}
