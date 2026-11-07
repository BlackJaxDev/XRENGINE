using XREngine.Core.Files;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Writes the source paths of cooked assets as portable references. A cooked build runs from another folder than the
/// project, so an absolute source path of the cook machine has no meaning there. Systems that identify an asset by its
/// source file, for example the stable texture keys of a cutscene residency manifest, need the same value in the editor
/// and in the cooked build. The policy never fails a cook.
/// </summary>
public static class CookedSourcePathPolicy
{
    /// <summary>
    /// Changes <see cref="XRAsset.OriginalPath"/> of an asset and of each asset that the cook writes with it to a portable
    /// <c>game://</c> or <c>engine://</c> reference when the path is in an asset folder of the current process. A path
    /// outside the asset folders stays as it is. Call it only on a copy that the cook deserialized, not on a live asset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cook writes every asset that the copy reaches through its serialized members into the cooked bytes. The policy
    /// finds them with <see cref="XRAssetGraphUtility.VisitReachableAssets"/>, so it also reaches assets that the graph
    /// refresh does not claim: the textures in the texture list of a material, and a texture that a source image backs.
    /// </para>
    /// <para>
    /// A live asset that the copy references (an asset that the asset services return as the same instance) is foreign:
    /// the graph refresh does not claim it, and the policy does not change it or the assets in it. If the graph refresh
    /// throws, or the walk stops at its limit, the policy logs one warning and still changes the paths that it reached.
    /// </para>
    /// </remarks>
    /// <param name="root">The deserialized asset to cook.</param>
    /// <returns>The number of paths that changed.</returns>
    public static int MakeSourcePathsPortable(XRAsset root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (IsLiveInstance(root))
            return 0;

        int changed = MakePortable(root) ? 1 : 0;
        Predicate<XRAsset> isForeign = asset => !ReferenceEquals(asset, root) && IsLiveInstance(asset);
        try
        {
            // The refresh releases live assets that the deserialization claimed as embedded in the copy.
            XRAssetGraphUtility.RefreshAssetGraph(root, isForeign);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.LogWarning($"[Cook] The asset graph of '{root.Name ?? root.GetType().Name}' could not be refreshed: {ex.Message}");
        }

        bool complete = XRAssetGraphUtility.VisitReachableAssets(root, isForeign, asset =>
        {
            if (MakePortable(asset))
                changed++;
        });
        if (!complete)
            Debug.LogWarning($"[Cook] The asset graph of '{root.Name ?? root.GetType().Name}' is too large to read fully, so some cooked source paths can stay absolute.");
        return changed;
    }

    /// <summary>True if the asset services track this instance, so it is a live asset and not part of the cooked copy.</summary>
    private static bool IsLiveInstance(XRAsset asset)
        => AssetSerializationServices.Current.TryGetAssetById(asset.ID, out XRAsset? live) && ReferenceEquals(live, asset);

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
