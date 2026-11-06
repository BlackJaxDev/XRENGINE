using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using XREngine.Data;

namespace XREngine.Cutscenes;

/// <summary>
/// The shared rules that connect an audio clip source file to its <see cref="AudioProxyAsset"/>. The proxy bake and
/// playback both use these rules, so playback finds the proxy that the bake made for the file that it opens.
/// </summary>
public static class CutsceneAudioProxySources
{
    private static readonly Lock HashCacheSync = new();
    private static readonly Dictionary<string, CachedHash> HashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the source reference of a proxy: the portable asset reference of the file (<c>game://</c> or <c>engine://</c>).
    /// If the file is outside the asset folders, the reference is the relative <paramref name="sourcePath"/> with <c>/</c>
    /// separators. A full path outside the asset folders has no reference, because a proxy must not store a local path.
    /// </summary>
    /// <param name="sourcePath">The <see cref="AudioTrackClip.SourcePath"/> of the clip.</param>
    /// <param name="fullPath">The full path from <see cref="AudioTrackEvaluator.ResolveSourcePath"/>.</param>
    /// <param name="reference">The source reference, or null.</param>
    /// <returns>True if the source can have a proxy.</returns>
    public static bool TryGetSourceReference(string sourcePath, string fullPath, [NotNullWhen(true)] out string? reference)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(fullPath);
        if (AssetSerializationServices.Current.TryCreatePortableAssetReference(fullPath, out reference))
            return true;

        reference = Path.IsPathRooted(sourcePath) ? null : sourcePath.Replace('\\', '/');
        return reference is not null;
    }

    /// <summary>
    /// Gets the XxHash64 of the bytes of a source file, or zero if the file does not exist.
    /// This reads the whole file, so do not call it in a frame.
    /// </summary>
    /// <param name="fullPath">The full path of the file.</param>
    public static ulong HashSourceFile(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (!File.Exists(fullPath))
            return 0;

        XxHash64 hash = new();
        using FileStream stream = File.OpenRead(fullPath);
        hash.Append(stream);
        return hash.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// Finds the proxy of a source reference in a list of proxies. The comparison ignores case.
    /// The method does not check the source hash.
    /// </summary>
    /// <param name="proxies">The proxies of the cutscene asset, or null.</param>
    /// <param name="reference">The source reference from <see cref="TryGetSourceReference"/>.</param>
    /// <returns>The proxy, or null.</returns>
    public static AudioProxyAsset? FindProxy(IReadOnlyList<AudioProxyAsset?>? proxies, string reference)
    {
        if (proxies is null)
            return null;

        for (int i = 0; i < proxies.Count; i++)
            if (proxies[i] is { } proxy && string.Equals(proxy.SourceReference, reference, StringComparison.OrdinalIgnoreCase))
                return proxy;
        return null;
    }

    /// <summary>
    /// Gets the hash of a source file. A file with the same length and the same last write time as at an earlier call
    /// gives the stored hash, so a recompile of an open cutscene does not read the file again.
    /// </summary>
    /// <param name="fullPath">The full path of the file.</param>
    internal static ulong GetSourceHash(string fullPath)
    {
        FileInfo info = new(fullPath);
        if (!info.Exists)
            return 0;

        long length = info.Length;
        DateTime written = info.LastWriteTimeUtc;
        lock (HashCacheSync)
            if (HashCache.TryGetValue(fullPath, out CachedHash cached) && cached.Length == length && cached.WrittenUtc == written)
                return cached.Hash;

        ulong hash = HashSourceFile(fullPath);
        lock (HashCacheSync)
            HashCache[fullPath] = new CachedHash(length, written, hash);
        return hash;
    }

    private readonly record struct CachedHash(long Length, DateTime WrittenUtc, ulong Hash);
}
