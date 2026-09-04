namespace XREngine.Rendering.Commands;

/// <summary>
/// Publication-ring-owned immutable resource image. Logical records and lookups
/// are retained by the contained table snapshots; strong source references keep
/// the exact texture objects available while any package or GPU lease pins the
/// publication.
/// </summary>
public sealed class AdvancedGpuResourcePublicationSnapshot
{
    private readonly XRTexture?[] _textureSources;
    private readonly AdvancedGpuHandle[] _textureSourceHandles;
    private readonly ulong[] _textureSourceGenerations;
    private int _textureSourceCount;

    internal AdvancedGpuResourcePublicationSnapshot(
        AdvancedGlobalResourceDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        Textures = database.Textures.CreatePublicationSnapshot(
            includeRecordImage: true);
        Samplers = database.Samplers.CreatePublicationSnapshot(
            includeRecordImage: true);
        _textureSources = new XRTexture?[checked((int)database.Textures.Capacity + 1)];
        _textureSourceHandles = new AdvancedGpuHandle[
            checked((int)database.Textures.Capacity)];
        _textureSourceGenerations = new ulong[checked((int)database.Textures.Capacity)];
    }

    public ulong Sequence { get; private set; }

    public AdvancedGlobalResourceDatabaseGenerations Generations { get; private set; }

    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedTextureRecord> Textures { get; }

    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedSamplerRecord> Samplers { get; }

    /// <summary>
    /// True only when every retained texture row has one exact strong source
    /// reference and every sampler row was captured by the boundary publisher.
    /// </summary>
    public bool HasCompleteSourceImage { get; private set; }

    public ReadOnlySpan<AdvancedGpuHandle> TextureSourceHandles
        => _textureSourceHandles.AsSpan(0, _textureSourceCount);

    public bool TryGetTextureSource(
        AdvancedGpuHandle handle,
        out XRTexture source)
    {
        source = null!;
        if (!HasCompleteSourceImage || !handle.IsValid ||
            handle.Index >= (uint)_textureSources.Length ||
            !Textures.TryGetDenseIndex(handle, out _))
        {
            return false;
        }

        XRTexture? candidate = _textureSources[checked((int)handle.Index)];
        if (candidate is null)
            return false;

        source = candidate;
        return true;
    }

    public bool TryGetTextureSource(
        AdvancedGpuHandle handle,
        out XRTexture source,
        out ulong sourceContentGeneration)
    {
        sourceContentGeneration = 0u;
        if (!TryGetTextureSource(handle, out source))
            return false;
        sourceContentGeneration = _textureSourceGenerations[checked((int)handle.Index)];
        return true;
    }

    internal bool TryCaptureTableState(
        ulong sequence,
        in AdvancedGlobalResourceDatabaseGenerations generations)
    {
        ClearTextureSources();
        if (sequence == 0u ||
            Textures.Sequence != sequence ||
            Samplers.Sequence != sequence)
        {
            Sequence = 0u;
            Generations = default;
            return false;
        }

        Sequence = sequence;
        Generations = generations;
        HasCompleteSourceImage = Textures.RecordCount == 0;
        return true;
    }

    internal bool TryBeginSourceCapture(
        ulong sequence,
        int textureCount,
        int samplerCount)
    {
        ClearTextureSources();
        if (sequence == 0u || sequence != Sequence ||
            textureCount < 0 || samplerCount < 0 ||
            textureCount > _textureSourceHandles.Length ||
            textureCount != Textures.RecordCount ||
            samplerCount != Samplers.RecordCount)
        {
            return false;
        }

        return true;
    }

    internal bool TryAddTextureSource(
        AdvancedGpuHandle handle,
        XRTexture source,
        ulong sourceContentGeneration)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!handle.IsValid || handle.Index >= (uint)_textureSources.Length ||
            !Textures.TryGetDenseIndex(handle, out _) ||
            _textureSources[checked((int)handle.Index)] is not null ||
            _textureSourceCount >= _textureSourceHandles.Length)
        {
            return false;
        }

        _textureSources[checked((int)handle.Index)] = source;
        _textureSourceGenerations[checked((int)handle.Index)] = sourceContentGeneration;
        _textureSourceHandles[_textureSourceCount++] = handle;
        return true;
    }

    internal bool TryCompleteSourceCapture(ulong sequence)
    {
        HasCompleteSourceImage = sequence != 0u &&
            sequence == Sequence &&
            _textureSourceCount == Textures.RecordCount;
        if (!HasCompleteSourceImage)
            ClearTextureSources();
        return HasCompleteSourceImage;
    }

    internal void AbortSourceCapture()
        => ClearTextureSources();

    private void ClearTextureSources()
    {
        for (int index = 0; index < _textureSourceCount; ++index)
        {
            AdvancedGpuHandle handle = _textureSourceHandles[index];
            if (handle.IsValid && handle.Index < (uint)_textureSources.Length)
                _textureSources[checked((int)handle.Index)] = null;
            if (handle.IsValid && handle.Index < (uint)_textureSourceGenerations.Length)
                _textureSourceGenerations[checked((int)handle.Index)] = 0u;
            _textureSourceHandles[index] = AdvancedGpuHandle.Invalid;
        }

        _textureSourceCount = 0;
        HasCompleteSourceImage = false;
    }
}
