using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    private object?[] _publishedLightSources = new object?[InitialCapacity];
    private AdvancedLightRecord[] _publishedLightRecords = new AdvancedLightRecord[InitialCapacity];
    private AdvancedGpuHandle[] _publishedLightHandles = new AdvancedGpuHandle[InitialCapacity];
    private object?[] _plannedLightSources = new object?[InitialCapacity];
    private AdvancedLightRecord[] _plannedLightRecords = new AdvancedLightRecord[InitialCapacity];
    private AdvancedGpuHandle[] _plannedLightHandles = new AdvancedGpuHandle[InitialCapacity];
    private int[] _plannedLightExistingIndices = new int[InitialCapacity];
    private bool[] _plannedLightRequiresReplace = new bool[InitialCapacity];
    private uint[] _publishedLightSeenStamps = new uint[InitialCapacity];
    private int _publishedLightCount;
    private int _plannedLightCount;
    private int _plannedLightMutationCount;
    private uint _publishedLightSeenGeneration;

    private bool TryPreflightGlobalResources(
        in AdvancedGlobalResourceCapture capture,
        out string reason)
    {
        ReadOnlySpan<object?> sources = capture.LightSources.Span;
        ReadOnlySpan<AdvancedLightRecord> records = capture.Lights.Span;
        if (sources.Length != records.Length)
        {
            reason = "The global-light source and record captures have different lengths.";
            return false;
        }

        EnsureGlobalResourcePlanCapacity(Math.Max(sources.Length, _publishedLightCount));
        BeginStampedPlan(ref _publishedLightSeenGeneration, _publishedLightSeenStamps);
        _plannedLightCount = sources.Length;
        int additions = 0;
        int replacements = 0;
        for (int index = 0; index < sources.Length; ++index)
        {
            object? source = sources[index];
            if (source is null || FindPlannedLightSource(source, index) >= 0)
            {
                reason = source is null
                    ? "The global-light capture contains a null source identity."
                    : "The global-light capture contains the same source identity more than once.";
                return false;
            }

            _plannedLightSources[index] = source;
            _plannedLightRecords[index] = records[index];
            int existingIndex = FindPublishedLightSource(source);
            _plannedLightExistingIndices[index] = existingIndex;
            if (existingIndex < 0)
            {
                _plannedLightRequiresReplace[index] = false;
                ++additions;
                continue;
            }

            _publishedLightSeenStamps[existingIndex] = _publishedLightSeenGeneration;
            bool changed = !RecordsEqual(
                in _publishedLightRecords[existingIndex],
                in records[index]);
            _plannedLightRequiresReplace[index] = changed;
            if (changed)
                ++replacements;
        }

        int tombstones = 0;
        for (int index = 0; index < _publishedLightCount; ++index)
            if (_publishedLightSeenStamps[index] != _publishedLightSeenGeneration)
                ++tombstones;

        _plannedLightMutationCount = checked(
            additions + replacements + tombstones);

        // TryAddLight emits both an add and the identity-stamping replacement.
        if (!Database.Resources.Lights.CanApply(
                additions,
                checked(replacements + additions),
                tombstones))
        {
            reason = "The canonical light table cannot accept the complete captured transition.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void ApplyPreflightedGlobalResources()
    {
        AdvancedGlobalResourceDatabase resources = Database.Resources;
        for (int index = 0; index < _plannedLightCount; ++index)
        {
            int existingIndex = _plannedLightExistingIndices[index];
            AdvancedGpuHandle handle;
            if (existingIndex < 0)
            {
                if (!resources.TryAddLight(in _plannedLightRecords[index], out handle))
                    throw new InvalidOperationException("A preflighted canonical light add failed.");
            }
            else
            {
                handle = _publishedLightHandles[existingIndex];
                if (_plannedLightRequiresReplace[index] &&
                    !resources.TryReplaceLight(handle, in _plannedLightRecords[index]))
                {
                    throw new InvalidOperationException("A preflighted canonical light replacement failed.");
                }
            }
            _plannedLightHandles[index] = handle;
        }

        for (int index = 0; index < _publishedLightCount; ++index)
            if (_publishedLightSeenStamps[index] != _publishedLightSeenGeneration &&
                !resources.RemoveLight(_publishedLightHandles[index]))
            {
                throw new InvalidOperationException("A preflighted canonical light retirement failed.");
            }

        Array.Copy(_plannedLightSources, _publishedLightSources, _plannedLightCount);
        Array.Copy(_plannedLightRecords, _publishedLightRecords, _plannedLightCount);
        Array.Copy(_plannedLightHandles, _publishedLightHandles, _plannedLightCount);
        if (_publishedLightCount > _plannedLightCount)
        {
            Array.Clear(
                _publishedLightSources,
                _plannedLightCount,
                _publishedLightCount - _plannedLightCount);
            Array.Clear(
                _publishedLightHandles,
                _plannedLightCount,
                _publishedLightCount - _plannedLightCount);
        }
        _publishedLightCount = _plannedLightCount;
    }

    private void EnsureGlobalResourcePlanCapacity(int required)
    {
        if (_publishedLightSources.Length >= required)
            return;
        int capacity = checked((int)NextPowerOfTwo(checked((uint)required + 1u)));
        Array.Resize(ref _publishedLightSources, capacity);
        Array.Resize(ref _publishedLightRecords, capacity);
        Array.Resize(ref _publishedLightHandles, capacity);
        Array.Resize(ref _plannedLightSources, capacity);
        Array.Resize(ref _plannedLightRecords, capacity);
        Array.Resize(ref _plannedLightHandles, capacity);
        Array.Resize(ref _plannedLightExistingIndices, capacity);
        Array.Resize(ref _plannedLightRequiresReplace, capacity);
        Array.Resize(ref _publishedLightSeenStamps, capacity);
        _publishedLightSeenGeneration = 0u;
    }

    private int FindPublishedLightSource(object source)
    {
        for (int index = 0; index < _publishedLightCount; ++index)
            if (ReferenceEquals(_publishedLightSources[index], source))
                return index;
        return -1;
    }

    private int FindPlannedLightSource(object source, int exclusiveEnd)
    {
        for (int index = 0; index < exclusiveEnd; ++index)
            if (ReferenceEquals(_plannedLightSources[index], source))
                return index;
        return -1;
    }

    private static bool RecordsEqual(
        in AdvancedLightRecord left,
        in AdvancedLightRecord right)
    {
        ReadOnlySpan<AdvancedLightRecord> leftRecord =
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in left), 1);
        ReadOnlySpan<AdvancedLightRecord> rightRecord =
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in right), 1);
        return MemoryMarshal.AsBytes(leftRecord).SequenceEqual(
            MemoryMarshal.AsBytes(rightRecord));
    }
}
