using System.Runtime.CompilerServices;
using XREngine.Data;

namespace XREngine.Rendering;

internal static partial class ShaderSourceResolver
{
    private static readonly ConditionalWeakTable<XRShader, CanonicalSourceOwner> CanonicalSourceOwners = new();

    internal static bool IsCanonicalSourceCurrent(XRShader shader)
    {
        CanonicalSourceOwner? owner;
        lock (CanonicalSourceOwners) CanonicalSourceOwners.TryGetValue(shader, out owner);
        return !shader.IsDestroyed && (owner is null ||
            RuntimeShaderServices.ServiceVersion == owner.ServiceVersion &&
            (RuntimeShaderServices.Current?.ShaderAssetCacheVersion ?? 0) == owner.AssetCacheVersion &&
            RuntimeSourcePublicationServices.IsCurrent(owner.Generation));
    }

    internal static void RecordCanonicalSourceOwner(XRShader shader, long generation,
        int serviceVersion, int assetCacheVersion)
    {
        lock (CanonicalSourceOwners)
        {
            CanonicalSourceOwners.Remove(shader);
            CanonicalSourceOwners.Add(shader, new(generation, serviceVersion, assetCacheVersion));
        }
    }

    /// <summary>Reads through the byte owner captured before the shader probe. No engine objects are allocated.</summary>
    internal static bool TryReadCanonicalSource(string path, ShaderSourceProviderOwner owner,
        RuntimeAssetReadLease read, out byte[] bytes)
    {
        bytes = [];
        read.EnsureCurrent();
        if (!owner.HostFileAccess) return false;
        bool exists = owner.FileBackend!.FileExists(path);
        read.EnsureCurrent();
        if (!IsOwnerCurrent(owner))
            throw new OperationCanceledException("ShaderSource.StaleOwner: retry canonical source recovery against the current host installation.");
        if (!exists) return false;
        try { bytes = read.ReadAllBytes(path); }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            read.EnsureCurrent();
            return false;
        }
        read.EnsureCurrent();
        return true;
    }

    private sealed class CanonicalSourceOwner(long generation, int serviceVersion, int assetCacheVersion)
    {
        public long Generation { get; } = generation;
        public int ServiceVersion { get; } = serviceVersion;
        public int AssetCacheVersion { get; } = assetCacheVersion;
    }
}
