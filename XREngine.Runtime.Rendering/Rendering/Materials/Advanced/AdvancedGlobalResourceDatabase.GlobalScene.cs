using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Canonical mutations for global scene records. The source publisher owns the
/// source-to-handle maps; this database only stamps stable identities and
/// preserves ABA-safe table semantics.
/// </summary>
public sealed partial class AdvancedGlobalResourceDatabase
{
    /// <summary>
    /// Adds a light-owned contiguous shadow group. The returned first handle is
    /// the canonical value stored in <see cref="AdvancedLightRecord.ShadowRecord"/>;
    /// physical rows remain contiguous until the next explicit compaction boundary.
    /// </summary>
    public bool TryAddShadowGroup(
        ReadOnlySpan<AdvancedShadowRecord> source,
        Span<AdvancedGpuHandle> handles,
        out AdvancedGpuHandle first)
    {
        first = AdvancedGpuHandle.Invalid;
        if (source.IsEmpty || handles.Length < source.Length ||
            // Each insertion is followed by an identity-stamping replacement.
            !Shadows.CanAddContiguous(source.Length, source.Length, 0) ||
            !Shadows.TryAddContiguous(source, handles))
        {
            return false;
        }

        for (int index = 0; index < source.Length; ++index)
        {
            AdvancedShadowRecord record = source[index];
            AdvancedGpuHandle handle = handles[index];
            record.StableShadowId = handle.Index;
            record.Generation = handle.Generation;
            if (!Shadows.TryReplace(handle, record))
                throw new InvalidOperationException("Newly inserted shadow group row could not be initialized.");
        }

        first = handles[0];
        IncrementGeneration(ref _shadowGeneration);
        return true;
    }

    public bool RemoveShadowGroup(ReadOnlySpan<AdvancedGpuHandle> handles)
    {
        if (handles.IsEmpty || !Shadows.CanApply(0, 0, handles.Length))
            return false;
        for (int index = 0; index < handles.Length; ++index)
            if (!Shadows.IsCurrent(handles[index]))
                return false;
        for (int index = 0; index < handles.Length; ++index)
            if (!Shadows.TryTombstone(handles[index]))
                throw new InvalidOperationException("A preflighted shadow-group retirement failed.");
        IncrementGeneration(ref _shadowGeneration);
        return true;
    }

    public bool TryAddLight(in AdvancedLightRecord source, out AdvancedGpuHandle handle)
    {
        AdvancedLightRecord record = source;
        record.StableLightId = 0u;
        record.Generation = 0u;
        if (!Lights.TryAdd(record, out handle))
            return false;

        record.StableLightId = handle.Index;
        record.Generation = handle.Generation;
        if (!Lights.TryReplace(handle, record))
            throw new InvalidOperationException("Newly inserted light could not be initialized.");
        IncrementGeneration(ref _lightGeneration);
        return true;
    }

    public bool TryReplaceLight(AdvancedGpuHandle handle, in AdvancedLightRecord source)
    {
        if (!Lights.IsCurrent(handle))
            return false;
        AdvancedLightRecord record = source;
        record.StableLightId = handle.Index;
        record.Generation = handle.Generation;
        if (!Lights.TryReplace(handle, record))
            return false;
        IncrementGeneration(ref _lightGeneration);
        return true;
    }

    public bool RemoveLight(AdvancedGpuHandle handle)
    {
        if (!Lights.TryTombstone(handle))
            return false;
        IncrementGeneration(ref _lightGeneration);
        return true;
    }

    public bool TryAddShadow(in AdvancedShadowRecord source, out AdvancedGpuHandle handle)
    {
        AdvancedShadowRecord record = source;
        record.StableShadowId = 0u;
        record.Generation = 0u;
        if (!Shadows.TryAdd(record, out handle))
            return false;

        record.StableShadowId = handle.Index;
        record.Generation = handle.Generation;
        if (!Shadows.TryReplace(handle, record))
            throw new InvalidOperationException("Newly inserted shadow could not be initialized.");
        IncrementGeneration(ref _shadowGeneration);
        return true;
    }

    public bool TryReplaceShadow(AdvancedGpuHandle handle, in AdvancedShadowRecord source)
    {
        if (!Shadows.IsCurrent(handle))
            return false;
        AdvancedShadowRecord record = source;
        record.StableShadowId = handle.Index;
        record.Generation = handle.Generation;
        if (!Shadows.TryReplace(handle, record))
            return false;
        IncrementGeneration(ref _shadowGeneration);
        return true;
    }

    public bool RemoveShadow(AdvancedGpuHandle handle)
    {
        if (!Shadows.TryTombstone(handle))
            return false;
        IncrementGeneration(ref _shadowGeneration);
        return true;
    }

    public bool TryAddProbe(in AdvancedProbeRecord source, out AdvancedGpuHandle handle)
    {
        AdvancedProbeRecord record = source;
        record.StableProbeId = 0u;
        record.Generation = 0u;
        if (!Probes.TryAdd(record, out handle))
            return false;

        record.StableProbeId = handle.Index;
        record.Generation = handle.Generation;
        if (!Probes.TryReplace(handle, record))
            throw new InvalidOperationException("Newly inserted probe could not be initialized.");
        IncrementGeneration(ref _probeGeneration);
        return true;
    }

    public bool TryReplaceProbe(AdvancedGpuHandle handle, in AdvancedProbeRecord source)
    {
        if (!Probes.IsCurrent(handle))
            return false;
        AdvancedProbeRecord record = source;
        record.StableProbeId = handle.Index;
        record.Generation = handle.Generation;
        if (!Probes.TryReplace(handle, record))
            return false;
        IncrementGeneration(ref _probeGeneration);
        return true;
    }

    public bool RemoveProbe(AdvancedGpuHandle handle)
    {
        if (!Probes.TryTombstone(handle))
            return false;
        IncrementGeneration(ref _probeGeneration);
        return true;
    }

    public bool TryAddEnvironment(in AdvancedEnvironmentRecord source, out AdvancedGpuHandle handle)
    {
        AdvancedEnvironmentRecord record = source;
        record.StableEnvironmentId = 0u;
        record.Generation = 0u;
        if (!Environments.TryAdd(record, out handle))
            return false;

        record.StableEnvironmentId = handle.Index;
        record.Generation = handle.Generation;
        if (!Environments.TryReplace(handle, record))
            throw new InvalidOperationException("Newly inserted environment could not be initialized.");
        IncrementGeneration(ref _environmentGeneration);
        return true;
    }

    public bool TryReplaceEnvironment(AdvancedGpuHandle handle, in AdvancedEnvironmentRecord source)
    {
        if (!Environments.IsCurrent(handle))
            return false;
        AdvancedEnvironmentRecord record = source;
        record.StableEnvironmentId = handle.Index;
        record.Generation = handle.Generation;
        if (!Environments.TryReplace(handle, record))
            return false;
        IncrementGeneration(ref _environmentGeneration);
        return true;
    }

    public bool RemoveEnvironment(AdvancedGpuHandle handle)
    {
        if (!Environments.TryTombstone(handle))
            return false;
        IncrementGeneration(ref _environmentGeneration);
        return true;
    }

    public bool TryAddDecal(in AdvancedDecalRecord source, out AdvancedGpuHandle handle)
    {
        AdvancedDecalRecord record = source;
        record.Identity = AdvancedGpuHandle.Invalid;
        if (!Decals.TryAdd(record, out handle))
            return false;

        record.Identity = handle;
        if (!Decals.TryReplace(handle, record))
            throw new InvalidOperationException("Newly inserted decal could not be initialized.");
        IncrementGeneration(ref _decalGeneration);
        return true;
    }

    public bool TryReplaceDecal(AdvancedGpuHandle handle, in AdvancedDecalRecord source)
    {
        if (!Decals.IsCurrent(handle))
            return false;
        AdvancedDecalRecord record = source;
        record.Identity = handle;
        if (!Decals.TryReplace(handle, record))
            return false;
        IncrementGeneration(ref _decalGeneration);
        return true;
    }

    public bool RemoveDecal(AdvancedGpuHandle handle)
    {
        if (!Decals.TryTombstone(handle))
            return false;
        IncrementGeneration(ref _decalGeneration);
        return true;
    }

    public bool TryAddGiResource(in AdvancedGiResourceRecord source, out AdvancedGpuHandle handle)
    {
        AdvancedGiResourceRecord record = source;
        record.StableResourceId = 0u;
        record.Generation = 0u;
        if (!GiResources.TryAdd(record, out handle))
            return false;

        record.StableResourceId = handle.Index;
        record.Generation = handle.Generation;
        if (!GiResources.TryReplace(handle, record))
            throw new InvalidOperationException("Newly inserted GI resource could not be initialized.");
        IncrementGeneration(ref _giResourceGeneration);
        return true;
    }

    public bool TryReplaceGiResource(AdvancedGpuHandle handle, in AdvancedGiResourceRecord source)
    {
        if (!GiResources.IsCurrent(handle))
            return false;
        AdvancedGiResourceRecord record = source;
        record.StableResourceId = handle.Index;
        record.Generation = handle.Generation;
        if (!GiResources.TryReplace(handle, record))
            return false;
        IncrementGeneration(ref _giResourceGeneration);
        return true;
    }

    public bool RemoveGiResource(AdvancedGpuHandle handle)
    {
        if (!GiResources.TryTombstone(handle))
            return false;
        IncrementGeneration(ref _giResourceGeneration);
        return true;
    }
}
