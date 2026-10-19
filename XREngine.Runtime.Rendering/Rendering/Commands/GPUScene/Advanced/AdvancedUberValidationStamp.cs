using XREngine.Core.Files;

namespace XREngine.Rendering.Commands;

/// <summary>Records the source owner and shader state of a strict Uber check.</summary>
internal readonly struct AdvancedUberValidationStamp
{
    private readonly ShaderSourceProviderOwner _owner;
    private readonly IRuntimeShaderServices? _services;
    private readonly int _serviceVersion;
    private readonly int _assetVersion;
    private readonly long _snippetVersion;
    private readonly XRShader? _canonicalShader;
    private readonly long _authoredRevision;
    private readonly long _sourceIdentityRevision;
    private readonly TextFile? _source;
    private readonly string? _sourcePath;
    private readonly string? _sourceText;
    private readonly string? _sourceName;
    private readonly string? _shaderPath;
    private readonly string? _shaderName;

    private AdvancedUberValidationStamp(XRShader shader)
    {
        _owner = ShaderSourceResolver.CaptureOwner();
        _services = RuntimeShaderServices.Current;
        _serviceVersion = RuntimeShaderServices.ServiceVersion;
        _assetVersion = _services?.ShaderAssetCacheVersion ?? 0;
        _snippetVersion = ShaderSourceResolver.RegisteredSnippetVersion;
        _canonicalShader = shader;
        _authoredRevision = shader.AuthoredSourceRevision;
        _sourceIdentityRevision = shader.SourceIdentityRevision;
        _source = shader.Source;
        _sourcePath = _source.FilePath;
        _sourceText = _source.Text;
        _sourceName = _source.Name;
        _shaderPath = shader.FilePath;
        _shaderName = shader.Name;
    }

    internal bool IsCurrent => _canonicalShader is { CanPublishSourceChanges: true } &&
        _owner.Equals(ShaderSourceResolver.CaptureOwner()) &&
        ReferenceEquals(_services, RuntimeShaderServices.Current) &&
        _serviceVersion == RuntimeShaderServices.ServiceVersion &&
        _assetVersion == (_services?.ShaderAssetCacheVersion ?? 0) &&
        _snippetVersion == ShaderSourceResolver.RegisteredSnippetVersion &&
        _authoredRevision == _canonicalShader.AuthoredSourceRevision &&
        _sourceIdentityRevision == _canonicalShader.SourceIdentityRevision &&
        ReferenceEquals(_source, _canonicalShader.Source) &&
        string.Equals(_sourcePath, _source?.FilePath, StringComparison.Ordinal) &&
        string.Equals(_sourceText, _source?.Text, StringComparison.Ordinal) &&
        string.Equals(_sourceName, _source?.Name, StringComparison.Ordinal) &&
        string.Equals(_shaderPath, _canonicalShader.FilePath, StringComparison.Ordinal) &&
        string.Equals(_shaderName, _canonicalShader.Name, StringComparison.Ordinal);

    /// <summary>Runs a strict check and keeps a stamp only for stable inputs.</summary>
    internal static bool TryValidate(XRMaterial material, out AdvancedUberValidationStamp stamp)
    {
        stamp = default;
        if (!material.TryGetUberMaterialState(out XRShader? shader, out _) || shader is null)
            return false;

        AdvancedUberValidationStamp candidate = new(shader);
        if (!material.TryGetUberMaterialState(out XRShader? verifiedShader, out _) ||
            !ReferenceEquals(shader, verifiedShader) || !candidate.IsCurrent)
            return false;

        stamp = candidate;
        return true;
    }
}
