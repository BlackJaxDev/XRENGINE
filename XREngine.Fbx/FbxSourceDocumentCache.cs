namespace XREngine.Fbx;

/// <summary>
/// Reuses decoded FBX source documents during one synchronous import. Consumers must not modify the documents.
/// </summary>
/// <remarks>
/// The cache holds one source revision and policy at a time. It does not retain structural source buffers
/// or runtime scene objects. The owner must dispose the cache when the import ends.
/// </remarks>
public sealed class FbxSourceDocumentCache : IDisposable
{
    private string? _sourcePath;
    private (long Length, long LastWriteTicks) _sourceStamp;
    private FbxSceneSemanticsPolicy? _policy;
    private FbxSemanticDocument? _semantic;
    private FbxGeometryDocument? _geometry;
    private FbxDeformerDocument? _deformers;
    private FbxAnimationDocument? _animations;
    private FbxAnimationDocument? _strictAnimations;
    private bool _disposed;

    /// <summary>
    /// Gets all model source documents from one revision, with permissive animation parsing.
    /// </summary>
    public (FbxSemanticDocument Semantic, FbxGeometryDocument Geometry,
        FbxDeformerDocument Deformers, FbxAnimationDocument Animations) GetModelDocuments(
        string path, FbxSceneSemanticsPolicy? policy = null)
    {
        SelectSource(path, policy);
        if (_semantic is not null && _geometry is not null && _deformers is not null && _animations is not null)
            return (_semantic, _geometry, _deformers, _animations);

        using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(_sourcePath!);
        FbxSemanticDocument semantic = _semantic ?? FbxSemanticParser.Parse(structural, _policy);
        FbxGeometryDocument geometry = _geometry ?? FbxGeometryParser.Parse(structural, semantic);
        FbxDeformerDocument deformers = _deformers ?? FbxDeformerParser.Parse(structural, semantic);
        FbxAnimationDocument animations = _animations ?? FbxAnimationParser.Parse(structural, semantic, deformers);
        ValidateSourceRevision();
        _semantic = semantic;
        _geometry = geometry;
        _deformers = deformers;
        _animations = animations;
        return (semantic, geometry, deformers, animations);
    }

    /// <summary>
    /// Gets the semantic snapshot for the source revision and policy.
    /// </summary>
    public FbxSemanticDocument GetSemantic(string path, FbxSceneSemanticsPolicy? policy = null)
    {
        SelectSource(path, policy);
        if (_semantic is not null)
            return _semantic;

        using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(_sourcePath!);
        FbxSemanticDocument semantic = FbxSemanticParser.Parse(structural, _policy);
        ValidateSourceRevision();
        return _semantic = semantic;
    }

    /// <summary>
    /// Gets the decoded geometry snapshot for the source revision and policy.
    /// </summary>
    public FbxGeometryDocument GetGeometry(string path, FbxSceneSemanticsPolicy? policy = null)
        => GetGeometrySnapshot(path, policy).Geometry;

    /// <summary>
    /// Gets semantic and geometry documents from one source revision.
    /// </summary>
    public (FbxSemanticDocument Semantic, FbxGeometryDocument Geometry) GetGeometrySnapshot(
        string path, FbxSceneSemanticsPolicy? policy = null)
    {
        SelectSource(path, policy);
        if (_semantic is not null && _geometry is not null)
            return (_semantic, _geometry);

        using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(_sourcePath!);
        FbxSemanticDocument semantic = _semantic ?? FbxSemanticParser.Parse(structural, _policy);
        FbxGeometryDocument geometry = FbxGeometryParser.Parse(structural, semantic);
        ValidateSourceRevision();
        _semantic = semantic;
        _geometry = geometry;
        return (semantic, geometry);
    }

    /// <summary>
    /// Gets the decoded skin and blendshape snapshot for the source revision and policy.
    /// </summary>
    public FbxDeformerDocument GetDeformers(string path, FbxSceneSemanticsPolicy? policy = null)
    {
        SelectSource(path, policy);
        if (_deformers is not null)
            return _deformers;

        using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(_sourcePath!);
        FbxSemanticDocument semantic = _semantic ?? FbxSemanticParser.Parse(structural, _policy);
        FbxDeformerDocument deformers = FbxDeformerParser.Parse(structural, semantic);
        ValidateSourceRevision();
        _semantic = semantic;
        return _deformers = deformers;
    }

    /// <summary>
    /// Gets animation data. Strict requests always use a snapshot that passed strict validation.
    /// </summary>
    public FbxAnimationDocument GetAnimations(
        string path,
        bool requireLosslessLinearAnimation = false,
        FbxSceneSemanticsPolicy? policy = null)
        => GetAnimationSnapshot(path, requireLosslessLinearAnimation, policy).Animations;

    /// <summary>
    /// Gets semantic and animation documents from one source revision, with the requested animation validation.
    /// </summary>
    public (FbxSemanticDocument Semantic, FbxAnimationDocument Animations) GetAnimationSnapshot(
        string path,
        bool requireLosslessLinearAnimation = false,
        FbxSceneSemanticsPolicy? policy = null)
    {
        SelectSource(path, policy);
        FbxAnimationDocument? cached = requireLosslessLinearAnimation ? _strictAnimations : _animations;
        if (_semantic is not null && cached is not null)
            return (_semantic, cached);

        using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(_sourcePath!);
        FbxSemanticDocument semantic = _semantic ?? FbxSemanticParser.Parse(structural, _policy);
        FbxDeformerDocument deformers = _deformers ?? FbxDeformerParser.Parse(structural, semantic);
        FbxAnimationDocument animations = FbxAnimationParser.Parse(
            structural, semantic, deformers, requireLosslessLinearAnimation);
        ValidateSourceRevision();
        _semantic = semantic;
        _deformers = deformers;
        if (requireLosslessLinearAnimation)
            _strictAnimations = animations;
        else
            _animations = animations;
        return (semantic, animations);
    }

    /// <summary>
    /// Releases the cached document references. Previously returned documents remain valid.
    /// </summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClearCore();
    }

    /// <summary>
    /// Releases all cached references and prevents further use.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        ClearCore();
        _disposed = true;
    }

    private void SelectSource(string path, FbxSceneSemanticsPolicy? policy)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string sourcePath = Path.GetFullPath(path);
        policy ??= FbxSceneSemanticsPolicy.Default;

        (long Length, long LastWriteTicks) stamp;
        try
        {
            stamp = ReadSourceStamp(sourcePath);
        }
        catch
        {
            ClearCore();
            throw;
        }

        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(_sourcePath, sourcePath, pathComparison) && _sourceStamp == stamp && _policy == policy)
            return;

        ClearCore();
        _sourcePath = sourcePath;
        _sourceStamp = stamp;
        _policy = policy;
    }

    private void ValidateSourceRevision()
    {
        try
        {
            if (ReadSourceStamp(_sourcePath!) != _sourceStamp)
                throw new IOException("The FBX source changed while its documents were being parsed.");
        }
        catch
        {
            ClearCore();
            throw;
        }
    }

    private static (long Length, long LastWriteTicks) ReadSourceStamp(string sourcePath)
    {
        FileInfo source = new(sourcePath);
        if (!source.Exists)
            throw new FileNotFoundException("The FBX source file does not exist.", sourcePath);
        return (source.Length, source.LastWriteTimeUtc.Ticks);
    }

    private void ClearCore()
    {
        _sourcePath = null;
        _sourceStamp = default;
        _policy = null;
        _semantic = null;
        _geometry = null;
        _deformers = null;
        _animations = null;
        _strictAnimations = null;
    }
}
