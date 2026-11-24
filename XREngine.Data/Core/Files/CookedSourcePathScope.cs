namespace XREngine.Core.Files;

/// <summary>
/// One open cook scope of <see cref="CookedSourcePathProjection"/>. It caches the projection of each path, records the
/// absolute paths outside every asset root that it cleared, and records the secret members that it wrote as their default
/// value. A scope is used on one execution context at a time.
/// </summary>
public sealed class CookedSourcePathScope : IDisposable
{
    private readonly Dictionary<string, string?> _projected = new(StringComparer.Ordinal);
    private readonly List<ClearedCookedSourcePath> _clearedPaths = [];
    private readonly HashSet<ClearedCookedSourcePath> _clearedPathSet = [];
    private readonly List<ClearedCookedSecret> _clearedSecrets = [];
    private readonly HashSet<ClearedCookedSecret> _clearedSecretSet = [];
    private bool _disposed;

    internal CookedSourcePathScope(CookedSourcePathScope? parent)
        => Parent = parent;

    internal CookedSourcePathScope? Parent { get; }

    /// <summary>The absolute source paths outside every asset root that the scope cleared, in the order of the first write.</summary>
    public IReadOnlyList<ClearedCookedSourcePath> ClearedPaths => _clearedPaths;

    /// <summary>The secret members that the scope wrote as their default value, in the order of the first write.</summary>
    public IReadOnlyList<ClearedCookedSecret> ClearedSecrets => _clearedSecrets;

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

        ClearedCookedSourcePath cleared = new(DescribeOwner(owner), path);
        if (_clearedPathSet.Add(cleared))
            _clearedPaths.Add(cleared);
        return null;
    }

    internal void RecordClearedSecret(Type ownerType, string member)
    {
        ClearedCookedSecret cleared = new(ownerType.Name, member);
        if (_clearedSecretSet.Add(cleared))
            _clearedSecrets.Add(cleared);
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

    /// <summary>Closes the scope. The records stay readable.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CookedSourcePathProjection.End(this);
    }
}
