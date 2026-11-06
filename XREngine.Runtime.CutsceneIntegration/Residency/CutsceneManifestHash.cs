using XREngine.Core.Files;

namespace XREngine.Cutscenes;

/// <summary>
/// Computes the content hash that tells if the residency manifest of a cutscene asset is current.
/// The hash covers the duration, the restore setting, the bindings, and the tracks with their clips. It does not cover
/// the manifest, the stored hash, the name, or the file path.
/// </summary>
/// <remarks>
/// The hash uses the cooked binary form of the bindings and the tracks, so each serialized member of a track or a clip
/// counts, also a member that a later change adds. The value is a 64-bit FNV-1a hash. It is not a security hash.
/// The calculation allocates, so call it at open, at a bake, or in an editor view, and not in each frame.
/// </remarks>
public static class CutsceneManifestHash
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>Computes the content hash of an asset.</summary>
    /// <param name="asset">The asset.</param>
    public static ulong Compute(CutsceneAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        ulong hash = OffsetBasis;
        hash = Add(hash, asset.Duration.Flicks);
        hash = Add(hash, asset.ForceRestoreAll ? 1L : 0L);
        hash = Add(hash, asset.Bindings.Count);
        hash = Add(hash, CookedBinarySerializer.Serialize(asset.Bindings));
        hash = Add(hash, asset.Tracks.Count);
        hash = Add(hash, CookedBinarySerializer.Serialize(asset.Tracks));
        return hash;
    }

    /// <summary>Gets the state of the manifest of an asset.</summary>
    /// <param name="asset">The asset.</param>
    public static ECutsceneManifestState GetState(CutsceneAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Manifest is null)
            return ECutsceneManifestState.Missing;
        return asset.ManifestContentHash == Compute(asset) ? ECutsceneManifestState.Current : ECutsceneManifestState.Stale;
    }

    private static ulong Add(ulong hash, long value)
    {
        for (int i = 0; i < sizeof(long); i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= Prime;
        }
        return hash;
    }

    private static ulong Add(ulong hash, ReadOnlySpan<byte> bytes)
    {
        hash = Add(hash, bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= Prime;
        }
        return hash;
    }
}
