namespace XREngine.Core.Files;

/// <summary>
/// One open cook scope of <see cref="CookedSourcePathProjection"/>. It caches the projection of each path and records the
/// absolute paths that are outside every asset root. A scope is used on one execution context at a time.
/// </summary>
public sealed class CookedSourcePathScope : IDisposable
{
    private readonly Dictionary<string, string?> _projected = new(StringComparer.Ordinal);
    private readonly List<CookedSourcePathFailure> _failures = [];
    private readonly HashSet<CookedSourcePathFailure> _failureSet = [];
    private bool _disposed;

    internal CookedSourcePathScope(CookedSourcePathScope? parent)
        => Parent = parent;

    internal CookedSourcePathScope? Parent { get; }

    /// <summary>The absolute source paths that the scope could not make portable, in the order of the first write.</summary>
    public IReadOnlyList<CookedSourcePathFailure> Failures => _failures;

    internal string? ProjectOriginalPath(object? owner, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || AssetReferencePath.IsPortable(path) || !IsMachinePath(path))
            return path;

        if (!_projected.TryGetValue(path, out string? reference))
        {
            reference = AssetSerializationServices.Current.TryCreatePortableAssetReference(path, out string? portable)
                ? portable
                : null;
            _projected.Add(path, reference);
        }

        if (reference is not null)
            return reference;

        CookedSourcePathFailure failure = new(DescribeOwner(owner), path);
        if (_failureSet.Add(failure))
            _failures.Add(failure);
        return path;
    }

    /// <summary>True if the path is absolute on this machine: a drive or root path, or a UNC path.</summary>
    private static bool IsMachinePath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) || Path.IsPathRooted(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string DescribeOwner(object? owner)
        => owner switch
        {
            XRAsset { Name: { Length: > 0 } name } => name,
            XRAsset asset => $"{asset.GetType().Name} {asset.ID}",
            null => "<unknown>",
            _ => owner.GetType().Name,
        };

    /// <summary>Closes the scope. The failures stay readable.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CookedSourcePathProjection.End(this);
    }
}
