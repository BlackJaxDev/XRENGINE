namespace XREngine.Rendering.Commands;

/// <summary>
/// In-place refresh of logical texture records. A streamed upload changes the record of a
/// texture that the scene already publishes (its size, mip count, and content generation),
/// but the texture object, its fallback, and its sampler stay the same. The logical texture
/// entry is found by the texture object, and a record replacement keeps its handle, so the
/// material bindings (texture handle and sampler handle) stay correct. The unchanged-row gate
/// stages such a refresh instead of failing, and the retained-row commit applies it inside
/// the open publication transaction.
/// </summary>
public sealed partial class AdvancedGpuResourcePublisher
{
    private StagedTextureRefresh[] _stagedTextureRefreshes = new StagedTextureRefresh[16];
    private int _stagedTextureRefreshCount;

    /// <summary>Gets the number of texture record refreshes that wait for the next commit.</summary>
    internal int StagedTextureRefreshCount => _stagedTextureRefreshCount;

    /// <summary>
    /// Stages a new record for the logical texture of <paramref name="binding"/> when the only
    /// difference from <paramref name="source"/> is the texture record, content generation, or
    /// source lifetime of the same texture object. Returns false, and stages nothing, when the
    /// binding names a different texture object, fallback, or sampler, when the new record is
    /// not valid, or when one gate pass encodes two different records for one texture. The
    /// staged state changes no publisher or database state until
    /// <see cref="ApplyStagedTextureRefreshes"/>.
    /// </summary>
    internal bool TryStageTextureRefresh(
        in AdvancedMaterialTextureBinding binding,
        in AdvancedGpuResourceBindingSource source)
    {
        if (source.Texture is null ||
            !binding.Texture.Handle.IsValid ||
            !binding.Sampler.Handle.IsValid ||
            binding.Texture.Fallback != source.Fallback)
            return false;

        int textureIndex = FindTextureByHandle(binding.Texture.Handle);
        int samplerIndex = FindSamplerByHandle(binding.Sampler.Handle);
        if (textureIndex < 0 || samplerIndex < 0)
            return false;

        ref readonly TextureEntry entry = ref _textures[textureIndex];
        if (!ReferenceEquals(entry.Source, source.Texture) ||
            !SamplerEquals(_samplers[samplerIndex].Record, source.SamplerRecord) ||
            !ValidateSource(in source, out _))
            return false;

        // The refresh keeps the default sampler of the entry. The database rejects the
        // replacement when that sampler is gone, so such an entry takes the full plan.
        AdvancedGpuHandle defaultSampler = entry.Record.DefaultSampler;
        if (defaultSampler.IsValid && !_database.Samplers.IsCurrent(defaultSampler))
            return false;

        // A texture that several materials bind is staged one time. Two different records
        // for one texture in one pass mean that the texture changed during the pass.
        for (int index = 0; index < _stagedTextureRefreshCount; ++index)
        {
            ref readonly StagedTextureRefresh staged = ref _stagedTextureRefreshes[index];
            if (staged.Handle == entry.Handle)
                return SourceEquals(in staged.Source, in source);
        }

        if (_stagedTextureRefreshCount == _stagedTextureRefreshes.Length)
            Array.Resize(ref _stagedTextureRefreshes, _stagedTextureRefreshes.Length * 2);
        _stagedTextureRefreshes[_stagedTextureRefreshCount++] = new StagedTextureRefresh(entry.Handle, source);
        return true;
    }

    /// <summary>
    /// Returns true when the canonical texture table can accept every staged refresh as a
    /// record replacement in the next publication.
    /// </summary>
    internal bool CanApplyStagedTextureRefreshes()
        => _database.Textures.CanApply(0, _stagedTextureRefreshCount, 0);

    /// <summary>
    /// Writes each staged record to its logical texture entry and to the canonical texture
    /// table, under the same handle. Call it only inside an open publication transaction, after
    /// <see cref="CanApplyStagedTextureRefreshes"/> returned true. The commit generation does
    /// not advance: no handle or binding changes, and the material witness of every material
    /// that binds a refreshed texture already changed through the texture source version.
    /// </summary>
    internal void ApplyStagedTextureRefreshes()
    {
        for (int index = 0; index < _stagedTextureRefreshCount; ++index)
        {
            ref readonly StagedTextureRefresh staged = ref _stagedTextureRefreshes[index];
            int textureIndex = FindTextureByHandle(staged.Handle);
            if (textureIndex < 0 || !ReferenceEquals(_textures[textureIndex].Source, staged.Source.Texture))
                throw new InvalidOperationException("A staged logical texture refresh lost its texture entry before the commit.");

            ref TextureEntry entry = ref _textures[textureIndex];
            AdvancedTextureRecord record = staged.Source.TextureRecord;
            record.DefaultSampler = entry.Record.DefaultSampler;
            if (!_database.TryReplaceTexture(entry.Handle, record))
                throw new InvalidOperationException("A preflighted logical texture refresh failed.");
            entry.Record = record;
            entry.SourceContentGeneration = staged.Source.SourceContentGeneration;
            entry.Lifetime = staged.Source.Lifetime;
        }
        ClearStagedTextureRefreshes();
    }

    /// <summary>
    /// Discards the staged refreshes, for example when the gate failed and the full plan
    /// computes the replacements through its own preflight.
    /// </summary>
    internal void ClearStagedTextureRefreshes()
    {
        if (_stagedTextureRefreshCount == 0)
            return;
        Array.Clear(_stagedTextureRefreshes, 0, _stagedTextureRefreshCount);
        _stagedTextureRefreshCount = 0;
    }

    /// <summary>One staged record refresh: the logical texture handle and the encoded source.</summary>
    private readonly struct StagedTextureRefresh(AdvancedGpuHandle handle, AdvancedGpuResourceBindingSource source)
    {
        public readonly AdvancedGpuHandle Handle = handle;
        public readonly AdvancedGpuResourceBindingSource Source = source;
    }
}
