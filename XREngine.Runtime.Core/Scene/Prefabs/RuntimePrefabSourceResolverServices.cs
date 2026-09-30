namespace XREngine.Scene.Prefabs;

/// <summary>Resolves prefab sources by their serialized asset identifier.</summary>
public interface IRuntimePrefabSourceResolver
{
    XRPrefabSource? Resolve(Guid prefabAssetId);
}

/// <summary>
/// Provides an optional runtime resolver for prefab variants whose base source was not
/// deserialized with the variant. Hosts install this capability for their asset lifetime.
/// </summary>
public static class RuntimePrefabSourceResolverServices
{
    private static readonly IRuntimePrefabSourceResolver Default = new NullResolver();
    private static IRuntimePrefabSourceResolver _current = Default;

    /// <summary>Gets the currently installed prefab source resolver.</summary>
    public static IRuntimePrefabSourceResolver Current => Volatile.Read(ref _current);

    /// <summary>Installs a resolver until the returned lease is disposed.</summary>
    public static IDisposable Install(IRuntimePrefabSourceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        IRuntimePrefabSourceResolver previous = Interlocked.Exchange(ref _current, resolver);
        return new InstallationLease(resolver, previous);
    }

    private sealed class InstallationLease(
        IRuntimePrefabSourceResolver installed,
        IRuntimePrefabSourceResolver previous) : IDisposable
    {
        private IRuntimePrefabSourceResolver? _installed = installed;

        public void Dispose()
        {
            IRuntimePrefabSourceResolver? installed = Interlocked.Exchange(ref _installed, null);
            if (installed is not null)
                Interlocked.CompareExchange(ref _current, previous, installed);
        }
    }

    private sealed class NullResolver : IRuntimePrefabSourceResolver
    {
        public XRPrefabSource? Resolve(Guid prefabAssetId) => null;
    }
}
