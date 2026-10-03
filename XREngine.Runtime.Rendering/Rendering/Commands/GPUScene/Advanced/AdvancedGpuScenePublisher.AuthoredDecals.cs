using System.Runtime.InteropServices;
using XREngine.Components;

namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    private AdvancedAuthoredDecalCaptureRow[] _plannedDecals = [];
    private AdvancedAuthoredDecalCaptureRow[] _publishedDecals = [];
    private AdvancedGpuHandle[] _plannedDecalHandles = [];
    private AdvancedGpuHandle[] _publishedDecalHandles = [];
    private AdvancedMaterialTextureBinding[] _plannedDecalBindings = [];
    private AdvancedMaterialTextureBinding[] _publishedDecalBindings = [];
    private int[] _plannedDecalExisting = [];
    private int[] _plannedDecalAcquire = [];
    private bool[] _plannedDecalReplace = [];
    private bool[] _publishedDecalSeen = [];
    private int _plannedDecalCount;
    private int _publishedDecalCount;
    private int _plannedDecalMutationCount;

    private bool TryPreflightAuthoredDecals(ReadOnlySpan<AdvancedAuthoredDecalCaptureRow> rows, out string reason)
    {
        EnsureAuthoredDecalCapacity(Math.Max(rows.Length, _publishedDecalCount));
        Array.Clear(_publishedDecalSeen, 0, _publishedDecalCount);
        EnsureGlobalResourceTransitionCapacity(checked(_resourceAcquireCount + rows.Length),
            checked(_resourceReleaseCount + _publishedDecalCount));
        rows.CopyTo(_plannedDecals);
        _plannedDecalCount = rows.Length;
        int additions = 0, replacements = 0, removals = 0;
        for (int index = 0; index < rows.Length; index++)
        {
            ref readonly AdvancedAuthoredDecalCaptureRow row = ref rows[index];
            int existing = FindPublishedDecal(row.Source);
            if (existing >= 0 && _publishedDecalSeen[existing])
            { reason = "The authored decal capture contains a duplicate source identity."; return false; }
            _plannedDecalExisting[index] = existing;
            if (existing >= 0) _publishedDecalSeen[existing] = true;
            bool resourcesChanged = existing < 0 || !_resourcePublisher.BindingMatches(
                in _publishedDecalBindings[existing], row.Image);
            bool replace = existing < 0 || resourcesChanged || !DecalRecordsEqual(row.Record, _publishedDecals[existing].Record);
            _plannedDecalReplace[index] = replace;
            _plannedDecalAcquire[index] = -1;
            if (existing < 0) additions++;
            else if (replace) replacements++;
            if (!resourcesChanged) continue;
            _plannedDecalAcquire[index] = _resourceAcquireCount;
            _resourceAcquireSources[_resourceAcquireCount++] = row.Image;
            if (existing >= 0) _resourceReleaseBindings[_resourceReleaseCount++] = _publishedDecalBindings[existing];
        }
        for (int index = 0; index < _publishedDecalCount; index++)
            if (!_publishedDecalSeen[index])
            {
                removals++;
                _resourceReleaseBindings[_resourceReleaseCount++] = _publishedDecalBindings[index];
            }
        _plannedDecalMutationCount = additions + replacements + removals;
        if (!Database.Resources.Decals.CanApply(additions, checked(additions + replacements + removals), removals))
        { reason = "The canonical authored decal table cannot accept the complete captured transition."; return false; }
        reason = string.Empty;
        return true;
    }

    private void ApplyPreflightedAuthoredDecals()
    {
        AdvancedGlobalResourceDatabase resources = Database.Resources;
        for (int index = 0; index < _plannedDecalCount; index++)
        {
            int existing = _plannedDecalExisting[index];
            int acquire = _plannedDecalAcquire[index];
            AdvancedMaterialTextureBinding binding = acquire >= 0
                ? _resourceAcquireBindings[acquire] : _publishedDecalBindings[existing];
            AdvancedDecalRecord record = _plannedDecals[index].Record;
            record.MaskTexture = binding.Texture;
            AdvancedGpuHandle handle;
            if (existing < 0)
            {
                if (!resources.TryAddDecal(record, out handle))
                    throw new InvalidOperationException("A preflighted authored decal add failed.");
            }
            else
            {
                handle = _publishedDecalHandles[existing];
                if (_plannedDecalReplace[index] && !resources.TryReplaceDecal(handle, record))
                    throw new InvalidOperationException("A preflighted authored decal replacement failed.");
            }
            _plannedDecalHandles[index] = handle;
            _plannedDecalBindings[index] = binding;
        }
        for (int index = 0; index < _publishedDecalCount; index++)
            if (!_publishedDecalSeen[index])
            {
                AdvancedGpuHandle handle = _publishedDecalHandles[index];
                if (!resources.Decals.TryGet(handle, out AdvancedDecalRecord retired))
                    throw new InvalidOperationException("A retiring authored decal lost its canonical record.");
                retired.Flags &= ~AdvancedDecalRecord.EnabledFlag;
                if (!resources.TryReplaceDecal(handle, retired) || !resources.RemoveDecal(handle))
                    throw new InvalidOperationException("A preflighted authored decal retirement failed.");
            }
        _plannedDecals.AsSpan(0, _plannedDecalCount).CopyTo(_publishedDecals);
        _plannedDecalHandles.AsSpan(0, _plannedDecalCount).CopyTo(_publishedDecalHandles);
        _plannedDecalBindings.AsSpan(0, _plannedDecalCount).CopyTo(_publishedDecalBindings);
        if (_publishedDecalCount > _plannedDecalCount)
            Array.Clear(_publishedDecals, _plannedDecalCount, _publishedDecalCount - _plannedDecalCount);
        _publishedDecalCount = _plannedDecalCount;
    }

    private void EnsureAuthoredDecalCapacity(int count)
    {
        if (_plannedDecals.Length >= count) return;
        int capacity = checked((int)NextPowerOfTwo((uint)count));
        Array.Resize(ref _plannedDecals, capacity);
        Array.Resize(ref _publishedDecals, capacity);
        Array.Resize(ref _plannedDecalHandles, capacity);
        Array.Resize(ref _publishedDecalHandles, capacity);
        Array.Resize(ref _plannedDecalBindings, capacity);
        Array.Resize(ref _publishedDecalBindings, capacity);
        Array.Resize(ref _plannedDecalExisting, capacity);
        Array.Resize(ref _plannedDecalAcquire, capacity);
        Array.Resize(ref _plannedDecalReplace, capacity);
        Array.Resize(ref _publishedDecalSeen, capacity);
    }

    private int FindPublishedDecal(DeferredDecalComponent source)
    {
        for (int index = 0; index < _publishedDecalCount; index++)
            if (ReferenceEquals(source, _publishedDecals[index].Source)) return index;
        return -1;
    }

    private static bool DecalRecordsEqual(AdvancedDecalRecord left, AdvancedDecalRecord right)
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref left, 1)).SequenceEqual(
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref right, 1)));
}
