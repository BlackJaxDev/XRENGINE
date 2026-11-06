using XREngine.Core.Files;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Writes the source paths of cooked assets as portable references. A cooked build runs from another folder than the
/// project, so an absolute source path of the cook machine has no meaning there. Systems that identify an asset by its
/// source file, for example the stable texture keys of a cutscene residency manifest, need the same value in the editor
/// and in the cooked build.
/// </summary>
public static class CookedSourcePathPolicy
{
    /// <summary>
    /// Changes <see cref="XRAsset.OriginalPath"/> of an asset and of each asset embedded in it to a portable
    /// <c>game://</c> or <c>engine://</c> reference when the path is in an asset folder of the current process. A path
    /// outside the asset folders stays as it is. Call it only on a copy that the cook deserialized, not on a live asset.
    /// </summary>
    /// <param name="root">The deserialized asset to cook.</param>
    /// <returns>The number of paths that changed.</returns>
    public static int MakeSourcePathsPortable(XRAsset root)
    {
        ArgumentNullException.ThrowIfNull(root);

        XRAssetGraphUtility.RefreshAssetGraph(root);
        int changed = MakePortable(root) ? 1 : 0;
        foreach (XRAsset embedded in root.EmbeddedAssets)
            if (embedded is not null && MakePortable(embedded))
                changed++;
        return changed;
    }

    private static bool MakePortable(XRAsset asset)
    {
        string? path = asset.OriginalPath;
        if (string.IsNullOrWhiteSpace(path) || AssetReferencePath.IsPortable(path))
            return false;
        if (!AssetSerializationServices.Current.TryCreatePortableAssetReference(path, out string? reference))
            return false;

        asset.OriginalPath = reference;
        return true;
    }
}
