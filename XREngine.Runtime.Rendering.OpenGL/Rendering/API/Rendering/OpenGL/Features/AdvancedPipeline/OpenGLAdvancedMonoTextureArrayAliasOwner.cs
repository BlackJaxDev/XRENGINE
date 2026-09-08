using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

/// <summary>Owns one-layer array views of immutable mono outputs for one fenced
/// Advanced slot. Entries retain their logical source texture as well as its GL name.</summary>
internal sealed class OpenGLAdvancedMonoTextureArrayAliasOwner : IDisposable
{
    private readonly OpenGLRenderer _renderer;
    // A core family needs five aliases and native shading adds five more.
    // Reserve bounded storage once; malformed/dynamic role names fail visibly.
    private const int MaximumAliasCount = 16;
    private readonly Dictionary<string, Alias> _aliases = new(MaximumAliasCount, StringComparer.Ordinal);
    private readonly HashSet<string> _usedThisFamily = new(MaximumAliasCount, StringComparer.Ordinal);
    private AdvancedVisibilityFamilyReservation _reservation;
    private long _pipelineGeneration;

    internal OpenGLAdvancedMonoTextureArrayAliasOwner(OpenGLRenderer renderer) => _renderer = renderer;

    internal bool TryGetOrCreate(string resourceName, XRTexture2D source, uint sourceId, long pipelineGeneration,
        out uint aliasId, out string reason)
    {
        aliasId = 0u;
        if (pipelineGeneration != _pipelineGeneration || source.IsDestroyed || !RawTextureIsImmutable(sourceId))
        {
            reason = $"OpenGL Advanced mono texture '{source.Name}' has mutable storage and cannot be safely viewed as a 2D array.";
            return false;
        }
        _renderer.RawGL.GetTextureLevelParameter(sourceId, 0, GLEnum.TextureInternalFormat, out int internalFormat);
        AliasKey key = new(source.ID, sourceId, internalFormat, source.Width, source.Height, pipelineGeneration);
        if (_aliases.TryGetValue(resourceName, out Alias existing))
        {
            if (existing.Key == key && _renderer.RawGL.IsTexture(existing.Id))
            {
                _usedThisFamily.Add(resourceName);
                aliasId = existing.Id;
                reason = "Ready";
                return true;
            }
            if (_usedThisFamily.Contains(resourceName))
            {
                reason = $"OpenGL Advanced resource '{resourceName}' changed after this family's first use; its submitted view must remain retained.";
                return false;
            }
            // First use after fenced slot reacquisition: the prior family's
            // view can now be replaced without accumulating retired sources.
            if (existing.Id != 0u)
                _renderer.RawGL.DeleteTexture(existing.Id);
            _aliases.Remove(resourceName);
        }
        if (_aliases.Count >= MaximumAliasCount)
        {
            reason = $"OpenGL Advanced mono output exceeds its {MaximumAliasCount}-role texture-view capacity.";
            return false;
        }

        uint view = _renderer.RawGL.GenTexture();
        if (view == 0u)
        {
            reason = "OpenGL could not allocate the mono Advanced texture-array view name.";
            return false;
        }
        _renderer.RawGL.TextureView(view, TextureTarget.Texture2DArray, sourceId, (GLEnum)internalFormat, 0u, 1u, 0u, 1u);
        GLEnum error = _renderer.RawGL.GetError();
        if (error != GLEnum.NoError || !_renderer.RawGL.IsTexture(view))
        {
            _renderer.RawGL.DeleteTexture(view);
            reason = $"OpenGL could not create a 2D-array view for mono Advanced texture '{source.Name}': {error}.";
            return false;
        }
        _renderer.RawGL.TextureParameter(view, GLEnum.TextureMinFilter, (int)GLEnum.Nearest);
        _renderer.RawGL.TextureParameter(view, GLEnum.TextureMagFilter, (int)GLEnum.Nearest);
        _renderer.RawGL.TextureParameter(view, GLEnum.TextureBaseLevel, 0);
        _renderer.RawGL.TextureParameter(view, GLEnum.TextureMaxLevel, 0);
        _renderer.RawGL.TextureParameter(view, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
        _renderer.RawGL.TextureParameter(view, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);
        _renderer.RawGL.TextureParameter(view, GLEnum.TextureWrapR, (int)GLEnum.ClampToEdge);
        _renderer.RawGL.TextureParameter(view, GLEnum.TextureCompareMode, (int)GLEnum.None);
        _aliases.Add(resourceName, new Alias(key, source, view));
        _usedThisFamily.Add(resourceName);
        aliasId = view;
        reason = "Ready";
        return true;
    }

    /// <summary>Called only after the containing slot has completed its prior fence and
    /// been reacquired. Resource generations are local to a pipeline; include the
    /// complete output reservation so bank reuse cannot retain another owner's views.</summary>
    internal void BeginReacquiredFamily(
        in AdvancedVisibilityFamilyReservation reservation,
        long pipelineGeneration)
    {
        if (_reservation != reservation || _pipelineGeneration != pipelineGeneration)
        {
            foreach (Alias alias in _aliases.Values)
                if (alias.Id != 0u)
                    _renderer.RawGL.DeleteTexture(alias.Id);
            _aliases.Clear();
        }
        _usedThisFamily.Clear();
        _reservation = reservation;
        _pipelineGeneration = pipelineGeneration;
    }

    internal bool MatchesFamily(in AdvancedVisibilityFamilyReservation reservation, long pipelineGeneration)
        => _reservation == reservation && _pipelineGeneration == pipelineGeneration;

    public void Dispose()
    {
        if (RuntimeEngine.IsRenderThread)
            foreach (Alias alias in _aliases.Values)
                if (alias.Id != 0u)
                    _renderer.RawGL.DeleteTexture(alias.Id);
        _aliases.Clear();
        _usedThisFamily.Clear();
    }

    private bool RawTextureIsImmutable(uint texture)
    {
        _renderer.RawGL.GetTextureParameter(texture, GLEnum.TextureImmutableFormat, out int immutable);
        return immutable != 0;
    }

    private readonly record struct AliasKey(Guid SourceOwnerId, uint SourceId, int InternalFormat, uint Width, uint Height, long PipelineGeneration);
    private readonly record struct Alias(AliasKey Key, XRTexture2D SourceOwner, uint Id);
}
