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
    private AdvancedShadowRecord[] _plannedShadowRecords = new AdvancedShadowRecord[InitialCapacity];
    private AdvancedGpuResourceBindingSource[] _plannedShadowSources = new AdvancedGpuResourceBindingSource[InitialCapacity];
    private AdvancedMaterialTextureBinding[] _plannedShadowBindings = new AdvancedMaterialTextureBinding[InitialCapacity];
    private AdvancedGpuHandle[] _plannedShadowHandles = new AdvancedGpuHandle[InitialCapacity];
    private int[] _plannedShadowLightIndices = new int[InitialCapacity];
    private AdvancedShadowRecord[] _plannedShadowSourceRecords = new AdvancedShadowRecord[InitialCapacity];
    private int[] _plannedShadowAcquireOffsets = new int[InitialCapacity];
    private int[] _plannedLightShadowStarts = new int[InitialCapacity];
    private int[] _plannedLightShadowCounts = new int[InitialCapacity];
    private bool[] _plannedLightShadowReplacements = new bool[InitialCapacity];
    private int[] _publishedLightShadowStarts = new int[InitialCapacity];
    private int[] _publishedLightShadowCounts = new int[InitialCapacity];
    private AdvancedShadowRecord[] _publishedShadowSourceRecords = new AdvancedShadowRecord[InitialCapacity];
    private AdvancedMaterialTextureBinding[] _publishedShadowBindings = new AdvancedMaterialTextureBinding[InitialCapacity];
    private AdvancedGpuHandle[] _publishedShadowHandles = new AdvancedGpuHandle[InitialCapacity];
    private int _plannedShadowCount;

    private bool TryPreflightGlobalResources(
        in AdvancedGlobalResourceCapture capture,
        out string reason)
    {
        reason = string.Empty;
        ReadOnlySpan<object?> sources = capture.LightSources.Span;
        ReadOnlySpan<AdvancedLightRecord> records = capture.Lights.Span;
        if (sources.Length != records.Length)
        {
            reason = "The global-light source and record captures have different lengths.";
            return false;
        }

        EnsureGlobalResourcePlanCapacity(Math.Max(sources.Length, _publishedLightCount));
        _plannedShadowCount = 0;
        Array.Fill(_plannedLightShadowStarts, -1, 0, sources.Length);
        Array.Clear(_plannedLightShadowCounts, 0, sources.Length);
        Array.Clear(_plannedLightShadowReplacements, 0, sources.Length);
        ReadOnlySpan<AdvancedShadowCaptureRow> shadowRows = capture.ShadowRows.Span;
        EnsureShadowPlanCapacity(shadowRows.Length);
        for (int shadowIndex = 0; shadowIndex < shadowRows.Length; ++shadowIndex)
        {
            ref readonly AdvancedShadowCaptureRow row = ref shadowRows[shadowIndex];
            if ((uint)row.LightIndex >= (uint)records.Length ||
                !AdvancedGpuResourceSourceEncoder.TryEncode(row.Texture, EAdvancedResourceFallback.Zero,
                    out _plannedShadowSources[_plannedShadowCount], out _, out reason))
            {
                reason = string.IsNullOrEmpty(reason) ? "A shadow row has no valid owning light." : reason;
                return false;
            }
            if (_plannedLightShadowStarts[row.LightIndex] < 0)
                _plannedLightShadowStarts[row.LightIndex] = _plannedShadowCount;
            else if (_plannedLightShadowStarts[row.LightIndex] + _plannedLightShadowCounts[row.LightIndex] != _plannedShadowCount)
            {
                reason = "The shadow capture must keep each light's shadow rows contiguous.";
                return false;
            }
            _plannedShadowRecords[_plannedShadowCount] = row.Record;
            _plannedShadowSourceRecords[_plannedShadowCount] = row.Record;
            _plannedShadowLightIndices[_plannedShadowCount++] = row.LightIndex;
            ++_plannedLightShadowCounts[row.LightIndex];
        }
        BeginStampedPlan(ref _publishedLightSeenGeneration, _publishedLightSeenStamps);
        _plannedLightCount = sources.Length;
        int additions = 0;
        int replacements = 0;
        int shadowAdditions = 0;
        int shadowTombstones = 0;
        int shadowAcquireCount = 0;
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
            int plannedShadowCount = _plannedLightShadowCounts[index];
            int plannedShadowStart = _plannedLightShadowStarts[index];
            if (existingIndex < 0)
            {
                _plannedLightShadowReplacements[index] = plannedShadowCount != 0;
                shadowAdditions += plannedShadowCount;
                shadowAcquireCount += plannedShadowCount;
                _plannedLightRequiresReplace[index] = false;
                ++additions;
                continue;
            }

            _publishedLightSeenStamps[existingIndex] = _publishedLightSeenGeneration;
            int publishedShadowCount = _publishedLightShadowCounts[existingIndex];
            int publishedShadowStart = _publishedLightShadowStarts[existingIndex];
            bool shadowChanged = publishedShadowCount != plannedShadowCount;
            if (!shadowChanged)
                for (int row = 0; row < plannedShadowCount; ++row)
                {
                    int plannedRow = plannedShadowStart + row;
                    int publishedRow = publishedShadowStart + row;
                    if (!ShadowRecordsEqual(in _plannedShadowSourceRecords[plannedRow], in _publishedShadowSourceRecords[publishedRow]) ||
                        !_resourcePublisher.BindingMatches(in _publishedShadowBindings[publishedRow], in _plannedShadowSources[plannedRow]))
                    {
                        shadowChanged = true;
                        break;
                    }
                }
            _plannedLightShadowReplacements[index] = shadowChanged;
            if (shadowChanged)
            {
                shadowAdditions += plannedShadowCount;
                shadowTombstones += publishedShadowCount;
                shadowAcquireCount += plannedShadowCount;
            }
            else if (publishedShadowCount != 0)
                _plannedLightRecords[index].ShadowRecord = _publishedShadowHandles[publishedShadowStart];
            // A replacement group receives its new root only during apply, so
            // force the owning light replacement even when its captured scalar
            // payload happened to compare equal (for example, no-shadow to
            // shadow-enabled transitions).
            bool changed = shadowChanged || !RecordsEqual(
                in _publishedLightRecords[existingIndex],
                in _plannedLightRecords[index]);
            _plannedLightRequiresReplace[index] = changed;
            if (changed)
                ++replacements;
        }

        int tombstones = 0;
        for (int index = 0; index < _publishedLightCount; ++index)
            if (_publishedLightSeenStamps[index] != _publishedLightSeenGeneration)
            {
                ++tombstones;
                shadowTombstones += _publishedLightShadowCounts[index];
            }

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

        // Material and shadow texture sources must share this one preflight;
        // the resource publisher keeps one scratch transaction at a time.
        int materialAcquireCount = _resourceAcquireCount;
        EnsureGlobalResourceTransitionCapacity(
            checked(materialAcquireCount + shadowAcquireCount),
            checked(_resourceReleaseCount + shadowTombstones));
        int acquireCursor = materialAcquireCount;
        int releaseCursor = _resourceReleaseCount;
        for (int lightIndex = 0; lightIndex < _plannedLightCount; ++lightIndex)
        {
            int existingIndex = _plannedLightExistingIndices[lightIndex];
            if (_plannedLightShadowReplacements[lightIndex])
            {
                if (existingIndex >= 0)
                {
                    int oldStart = _publishedLightShadowStarts[existingIndex];
                    int oldCount = _publishedLightShadowCounts[existingIndex];
                    if (oldCount != 0)
                        _publishedShadowBindings.AsSpan(oldStart, oldCount).CopyTo(_resourceReleaseBindings.AsSpan(releaseCursor, oldCount));
                    releaseCursor += oldCount;
                }
                int start = _plannedLightShadowStarts[lightIndex];
                int count = _plannedLightShadowCounts[lightIndex];
                for (int row = 0; row < count; ++row)
                {
                    int plannedRow = start + row;
                    _plannedShadowAcquireOffsets[plannedRow] = acquireCursor;
                    _resourceAcquireSources[acquireCursor++] = _plannedShadowSources[plannedRow];
                }
            }
        }
        for (int index = 0; index < _publishedLightCount; ++index)
            if (_publishedLightSeenStamps[index] != _publishedLightSeenGeneration)
            {
                int oldStart = _publishedLightShadowStarts[index];
                int oldCount = _publishedLightShadowCounts[index];
                if (oldCount != 0)
                    _publishedShadowBindings.AsSpan(oldStart, oldCount).CopyTo(_resourceReleaseBindings.AsSpan(releaseCursor, oldCount));
                releaseCursor += oldCount;
            }
        _resourceAcquireCount = acquireCursor;
        _resourceReleaseCount = releaseCursor;
        if (!_resourcePublisher.TryPreflightTransition(
                _resourceAcquireSources.AsSpan(0, _resourceAcquireCount),
                _resourceReleaseBindings.AsSpan(0, _resourceReleaseCount), out reason) ||
            // Group insertion stamps identity and the publisher then stamps the
            // shared first-row offset, so each row has two replacements.
            !Database.Resources.Shadows.CanApply(shadowAdditions, checked(shadowAdditions * 2), shadowTombstones) ||
            // Group additions happen before old groups are tombstoned. Reserve
            // an actual physical suffix so apply cannot discover fragmentation
            // after texture leases have already been acquired.
            !Database.Resources.Shadows.CanReserveContiguousAppend(shadowAdditions))
        {
            reason = string.IsNullOrEmpty(reason) ? "The canonical shadow table cannot accept the captured rows." : reason;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void ApplyPreflightedGlobalResources()
    {
        AdvancedGlobalResourceDatabase resources = Database.Resources;
        for (int lightIndex = 0; lightIndex < _plannedLightCount; ++lightIndex)
        {
            int groupStart = _plannedLightShadowStarts[lightIndex];
            int groupCount = _plannedLightShadowCounts[lightIndex];
            if (!_plannedLightShadowReplacements[lightIndex])
            {
                int existingIndex = _plannedLightExistingIndices[lightIndex];
                if (groupCount != 0)
                {
                    int publishedStart = _publishedLightShadowStarts[existingIndex];
                    _publishedShadowBindings.AsSpan(publishedStart, groupCount).CopyTo(_plannedShadowBindings.AsSpan(groupStart, groupCount));
                    _publishedShadowHandles.AsSpan(publishedStart, groupCount).CopyTo(_plannedShadowHandles.AsSpan(groupStart, groupCount));
                    for (int row = 0; row < groupCount; ++row)
                        _plannedShadowRecords[groupStart + row] = _publishedShadowSourceRecords[publishedStart + row];
                }
                continue;
            }
            if (groupCount == 0)
                continue;
            for (int row = 0; row < groupCount; ++row)
            {
                int shadowIndex = groupStart + row;
                _plannedShadowBindings[shadowIndex] = _resourceAcquireBindings[_plannedShadowAcquireOffsets[shadowIndex]];
                _plannedShadowRecords[shadowIndex].Texture = _plannedShadowBindings[shadowIndex].Texture;
            }
            if (!resources.TryAddShadowGroup(_plannedShadowRecords.AsSpan(groupStart, groupCount), _plannedShadowHandles.AsSpan(groupStart, groupCount), out AdvancedGpuHandle first))
                throw new InvalidOperationException("A preflighted canonical shadow-group add failed.");
            if (!resources.Shadows.TryGetPhysicalIndex(first, out uint firstPhysicalRow))
                throw new InvalidOperationException("Newly inserted shadow group lost its first physical row.");
            for (int row = groupStart; row < groupStart + groupCount; ++row)
            {
                _plannedShadowRecords[row].CascadeOffset = firstPhysicalRow;
                _plannedShadowRecords[row].CascadeCount = (uint)groupCount;
                if (!resources.TryReplaceShadow(_plannedShadowHandles[row], _plannedShadowRecords[row]))
                    throw new InvalidOperationException("A preflighted canonical shadow-group stamp failed.");
            }
            _plannedLightRecords[lightIndex].ShadowRecord = first;
        }
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

        for (int index = 0; index < _publishedLightCount; ++index)
        {
            bool retired = _publishedLightSeenStamps[index] != _publishedLightSeenGeneration;
            int replacementIndex = retired ? -1 : FindPlannedLightSource(_publishedLightSources[index]!, _plannedLightCount);
            if (!retired && !_plannedLightShadowReplacements[replacementIndex])
                continue;
            int start = _publishedLightShadowStarts[index];
            int count = _publishedLightShadowCounts[index];
            if (count != 0 && !resources.RemoveShadowGroup(_publishedShadowHandles.AsSpan(start, count)))
                throw new InvalidOperationException("A preflighted canonical shadow-group retirement failed.");
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
        EnsurePublishedShadowCapacity(_plannedShadowCount);
        _plannedShadowSourceRecords.AsSpan(0, _plannedShadowCount).CopyTo(_publishedShadowSourceRecords);
        _plannedShadowBindings.AsSpan(0, _plannedShadowCount).CopyTo(_publishedShadowBindings);
        _plannedShadowHandles.AsSpan(0, _plannedShadowCount).CopyTo(_publishedShadowHandles);
        Array.Copy(_plannedLightShadowStarts, _publishedLightShadowStarts, _plannedLightCount);
        Array.Copy(_plannedLightShadowCounts, _publishedLightShadowCounts, _plannedLightCount);
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
        Array.Resize(ref _plannedLightShadowStarts, capacity);
        Array.Resize(ref _plannedLightShadowCounts, capacity);
        Array.Resize(ref _plannedLightShadowReplacements, capacity);
        Array.Resize(ref _publishedLightShadowStarts, capacity);
        Array.Resize(ref _publishedLightShadowCounts, capacity);
        _publishedLightSeenGeneration = 0u;
    }

    private void EnsureShadowPlanCapacity(int required)
    {
        if (_plannedShadowRecords.Length >= required)
            return;
        int capacity = checked((int)NextPowerOfTwo(checked((uint)required + 1u)));
        Array.Resize(ref _plannedShadowRecords, capacity);
        Array.Resize(ref _plannedShadowSources, capacity);
        Array.Resize(ref _plannedShadowBindings, capacity);
        Array.Resize(ref _plannedShadowHandles, capacity);
        Array.Resize(ref _plannedShadowLightIndices, capacity);
        Array.Resize(ref _plannedShadowSourceRecords, capacity);
        Array.Resize(ref _plannedShadowAcquireOffsets, capacity);
    }

    private void EnsurePublishedShadowCapacity(int required)
    {
        if (_publishedShadowHandles.Length >= required)
            return;
        int capacity = checked((int)NextPowerOfTwo(checked((uint)required + 1u)));
        Array.Resize(ref _publishedShadowSourceRecords, capacity);
        Array.Resize(ref _publishedShadowBindings, capacity);
        Array.Resize(ref _publishedShadowHandles, capacity);
    }

    private void EnsureGlobalResourceTransitionCapacity(int acquireRequired, int releaseRequired)
    {
        if (_resourceAcquireSources.Length < acquireRequired)
        {
            int capacity = checked((int)NextPowerOfTwo(checked((uint)acquireRequired + 1u)));
            Array.Resize(ref _resourceAcquireSources, capacity);
            Array.Resize(ref _resourceAcquireBindings, capacity);
        }
        if (_resourceReleaseBindings.Length < releaseRequired)
            Array.Resize(ref _resourceReleaseBindings, checked((int)NextPowerOfTwo(checked((uint)releaseRequired + 1u))));
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

    private static bool ShadowRecordsEqual(
        in AdvancedShadowRecord left,
        in AdvancedShadowRecord right)
    {
        ReadOnlySpan<AdvancedShadowRecord> leftRecord =
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in left), 1);
        ReadOnlySpan<AdvancedShadowRecord> rightRecord =
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in right), 1);
        return MemoryMarshal.AsBytes(leftRecord).SequenceEqual(
            MemoryMarshal.AsBytes(rightRecord));
    }
}
