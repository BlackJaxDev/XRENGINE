using System.Reflection;
using System.Runtime.InteropServices;

namespace XREngine.Core.Files;

/// <summary>
/// Shares strict decoded-allocation and nesting limits across one runtime asset's nested cooked readers.
/// Fixed engine MemoryPack envelopes are preflighted; opaque non-asset MemoryPack objects are rejected.
/// Registered RuntimeBinaryV1 and custom serializers, constructors, and callbacks remain responsible
/// for allocations outside these readers. This is not a process-wide or retained-world heap limit.
/// </summary>
public sealed class CookedBinaryReadBudget
{
    private static readonly AsyncLocal<CookedBinaryReadBudget?> Active = new();
    private readonly long _maximumBytes;
    private readonly int _maximumDepth;
    private readonly int _maximumCollectionCount;
    private long _allocatedBytes;
    private int _depth;

    private CookedBinaryReadBudget(long maximumBytes, int maximumDepth, int maximumCollectionCount)
        => (_maximumBytes, _maximumDepth, _maximumCollectionCount) = (maximumBytes, maximumDepth, maximumCollectionCount);

    internal static bool IsActive => Active.Value is not null;

    /// <summary>Bounds a synchronous catalog decode; nested scopes cannot reset the outer budget.</summary>
    public static IDisposable BeginRuntimeCatalogRead(long maximumDecodedBytes = 64L * 1024 * 1024,
        int maximumDepth = 128, int maximumCollectionCount = 1_048_576)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCollectionCount);
        CookedBinaryReadBudget? previous = Active.Value;
        Active.Value = previous ?? new(maximumDecodedBytes, maximumDepth, maximumCollectionCount);
        return new Installation(previous);
    }

    internal static void Reserve(long count, long elementBytes = 1)
    {
        if (count < 0 || elementBytes < 0)
            throw new InvalidDataException("CookedBinary.InvalidLength: negative allocation size.");
        CookedBinaryReadBudget? budget = Active.Value;
        if (budget is null)
            return;
        long remaining = budget._maximumBytes - budget._allocatedBytes;
        if (elementBytes != 0 && count > remaining / elementBytes)
            throw new InvalidDataException("CookedBinary.AllocationBudgetExceeded: decoded allocations exceed the runtime asset budget.");
        budget._allocatedBytes += count * elementBytes;
    }

    internal static void ValidateCount(int count)
    {
        if (count < 0)
            throw new InvalidDataException("CookedBinary.InvalidLength: negative collection count.");
        if (Active.Value is { } budget && count > budget._maximumCollectionCount)
            throw new InvalidDataException("CookedBinary.CollectionBudgetExceeded: collection count exceeds the runtime asset budget.");
    }

    internal static ValueScope EnterValue()
    {
        CookedBinaryReadBudget? budget = Active.Value;
        if (budget is not null)
        {
            if (budget._depth >= budget._maximumDepth)
                throw new InvalidDataException("CookedBinary.DepthBudgetExceeded: nested values exceed the runtime asset budget.");
            // Include per-value boxing, object headers, and ordinary collection bookkeeping.
            Reserve(64);
            budget._depth++;
        }
        return new ValueScope(budget);
    }

    internal static void ReserveArray(int count, Type elementType)
    {
        if (Active.Value is null)
            return;
        Reserve(count, ElementSizeUpperBound(elementType));
    }

    internal static void ReserveInstance(Type type)
    {
        if (Active.Value is null)
            return;
        if (type.IsValueType)
        {
            Reserve(ElementSizeUpperBound(type));
            return;
        }
        for (Type? current = type; current is not null; current = current.BaseType)
            foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                Reserve(ElementSizeUpperBound(field.FieldType));
    }

    private static long ElementSizeUpperBound(Type type)
    {
        if (!type.IsValueType || type.IsPointer)
            return IntPtr.Size;
        if (type.IsEnum)
            type = Enum.GetUnderlyingType(type);
        if (type.IsPrimitive || type == typeof(decimal) || type == typeof(IntPtr) || type == typeof(UIntPtr))
            return Math.Max(8, Marshal.SizeOf(type));
        // Account for managed fields as references; round every field up for alignment.
        // This is deliberately conservative and does not use unmanaged marshaling sizes for managed structs.
        long size = 8;
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            size = checked(size + ElementSizeUpperBound(field.FieldType));
        return Math.Max(size, type.StructLayoutAttribute?.Size ?? 0);
    }

    internal readonly struct ValueScope(CookedBinaryReadBudget? budget) : IDisposable
    {
        public void Dispose()
        {
            if (budget is not null)
                budget._depth--;
        }
    }

    private sealed class Installation(CookedBinaryReadBudget? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Active.Value = previous;
        }
    }
}
