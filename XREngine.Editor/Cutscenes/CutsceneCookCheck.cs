using XREngine.Cutscenes;

namespace XREngine.Editor.Cutscenes;

/// <summary>
/// Checks a cutscene asset before a cook. A shipping cook rejects an asset whose residency manifest is missing or stale,
/// because playback would then use the approximate manifest. A development cook accepts it; the editor shows the warning.
/// </summary>
/// <remarks>The project builder does not call this check yet. Call it for each <see cref="CutsceneAsset"/> before the asset is cooked.</remarks>
public static class CutsceneCookCheck
{
    /// <summary>Gets the reason why a cook must reject an asset.</summary>
    /// <param name="asset">The asset to cook.</param>
    /// <param name="shippingCook">True for a shipping cook.</param>
    /// <returns>The reason, or null if the cook can use the asset.</returns>
    public static string? GetRejection(CutsceneAsset asset, bool shippingCook)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!shippingCook)
            return null;

        return CutsceneManifestHash.GetState(asset) switch
        {
            ECutsceneManifestState.Missing => $"The cutscene '{asset.Name}' has no residency manifest. A shipping cook needs a baked manifest. Bake it in the cutscene sequencer.",
            ECutsceneManifestState.Stale => $"The residency manifest of the cutscene '{asset.Name}' is stale, because the cutscene changed after the bake. Bake it again in the cutscene sequencer.",
            _ => null,
        };
    }
}
