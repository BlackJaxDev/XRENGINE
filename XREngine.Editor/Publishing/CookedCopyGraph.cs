using XREngine.Core.Files;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Prepares the asset graph of a copy that the cook deserialized. The YAML load can claim a live asset that the copy
/// references (an asset that the asset services return as the same instance) as embedded in the copy. That would change the
/// source asset of a live editor asset, so the cook releases these claims before it serializes the copy.
/// </summary>
/// <remarks>
/// The cook writes portable source paths at write time (<see cref="CookedSourcePathProjection"/>), so this step does not
/// change any path.
/// </remarks>
public static class CookedCopyGraph
{
    /// <summary>
    /// Refreshes the asset graph of a deserialized copy, and treats each live asset as foreign, so that the copy does not
    /// claim it. A refresh failure fails the cook: the exception goes to the caller.
    /// </summary>
    /// <param name="root">The deserialized asset to cook.</param>
    public static void ReleaseLiveAssets(XRAsset root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (IsLiveInstance(root))
            return;

        XRAssetGraphUtility.RefreshAssetGraph(root, asset => !ReferenceEquals(asset, root) && IsLiveInstance(asset));
    }

    /// <summary>True if the asset services track this instance, so it is a live asset and not part of the cooked copy.</summary>
    private static bool IsLiveInstance(XRAsset asset)
        => AssetSerializationServices.Current.TryGetAssetById(asset.ID, out XRAsset? live) && ReferenceEquals(live, asset);
}
