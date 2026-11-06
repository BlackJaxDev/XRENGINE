namespace XREngine.Cutscenes;

/// <summary>
/// Checks that the textures of a baked residency manifest are loaded textures of the scene. The content hash does not see
/// the scene, so a manifest can be current and still name assets that the scene does not have.
/// </summary>
public static class CutsceneManifestMatch
{
    /// <summary>
    /// The largest fraction of the textures of a baked manifest that can be missing. Above it, the manifest does not match the
    /// scene (<see cref="ECutsceneManifestState.Unmatched"/>). For a small manifest one missing texture can be enough, for
    /// example 1 of 9.
    /// </summary>
    public const double MaxUnresolvedTextureFraction = 0.1;

    /// <summary>True if more than <see cref="MaxUnresolvedTextureFraction"/> of the textures are not found.</summary>
    /// <param name="textures">The number of textures of the manifest.</param>
    /// <param name="unresolved">The number of textures that were not found.</param>
    public static bool IsUnmatched(int textures, int unresolved)
        => unresolved > textures * MaxUnresolvedTextureFraction;

    /// <summary>Resolves each texture asset of a manifest one time, and counts the textures that were not found.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="canResolve">Returns true if an asset is found.</param>
    /// <param name="resolved">Receives true for each asset that is found, and for each asset that is not a texture.</param>
    /// <param name="textures">The number of texture assets.</param>
    /// <param name="unresolved">The number of texture assets that were not found.</param>
    public static void Resolve(CutsceneResidencyManifest manifest, Func<Guid, bool> canResolve, bool[] resolved, out int textures, out int unresolved)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(canResolve);
        ArgumentNullException.ThrowIfNull(resolved);

        Guid[] assets = manifest.Assets;
        EResidencyAssetKind[] kinds = manifest.AssetKinds;
        if (resolved.Length < assets.Length)
            throw new ArgumentException("The result array is shorter than the asset table.", nameof(resolved));

        textures = 0;
        unresolved = 0;
        for (int i = 0; i < assets.Length; i++)
        {
            if (kinds[i] != EResidencyAssetKind.Texture)
            {
                resolved[i] = true;
                continue;
            }

            textures++;
            resolved[i] = canResolve(assets[i]);
            if (!resolved[i])
                unresolved++;
        }
    }
}
