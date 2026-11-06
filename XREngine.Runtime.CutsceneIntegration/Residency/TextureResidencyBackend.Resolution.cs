using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Cutscenes;

public sealed partial class TextureResidencyBackend
{
    private readonly Action<Guid, List<XRTexture2D>> _resolveTextures;
    private readonly TextureResidencyKeyIndex? _keys;
    private readonly Lock _resolveSync = new();
    private readonly Dictionary<Guid, XRTexture2D[]> _resolved = [];
    private readonly List<XRTexture2D> _resolveScratch = [];
    private bool _deadHitRebuildUsed;

    /// <summary>True while model imports that register textures with the shared streaming manager are active.</summary>
    public static bool AreModelImportsActive
        => ImportedTextureStreamingManager.Instance.HasActiveImportedModelImports;

    /// <inheritdoc />
    /// <remarks>
    /// True if the asset is a live texture now. It does not check that streaming tracks the texture. The result stays until
    /// the next <see cref="BeginSelection"/>, and <see cref="Request"/> uses the same textures.
    /// </remarks>
    public bool CanResolve(Guid assetId)
        => !_disposed && ResolveCached(assetId).Length > 0;

    /// <inheritdoc />
    /// <remarks>It clears the resolved textures and lets the key index build one more time.</remarks>
    public void BeginSelection()
    {
        lock (_resolveSync)
        {
            _resolved.Clear();
            _deadHitRebuildUsed = false;
        }
        _keys?.AllowRebuild();
    }

    /// <summary>
    /// Gets the manifest state of a cutscene for a view with no open residency session, for example an editor panel. It
    /// gives the state that a session would select now: the content hash state, <see cref="ECutsceneManifestState.Invalid"/>
    /// for data that is not valid, or <see cref="ECutsceneManifestState.Unmatched"/> when too many baked textures are not
    /// loaded and no model import is active. It scans the object cache one time, so do not call it in each frame.
    /// </summary>
    /// <param name="asset">The cutscene asset.</param>
    /// <param name="assets">The asset manager that holds the loaded textures.</param>
    public static ECutsceneManifestState GetManifestState(CutsceneAsset asset, AssetManager assets)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(assets);

        ECutsceneManifestState state = CutsceneManifestHash.GetState(asset);
        if (state != ECutsceneManifestState.Current)
            return state;

        CutsceneResidencyManifest manifest = asset.Manifest!;
        try
        {
            manifest.Validate();
        }
        catch (ArgumentException)
        {
            return ECutsceneManifestState.Invalid;
        }

        TextureResidencyKeyIndex keys = new();
        List<XRTexture2D> scratch = [];
        bool[] resolved = new bool[manifest.Assets.Length];
        CutsceneManifestMatch.Resolve(manifest, assetId =>
        {
            scratch.Clear();
            ResolveLoadedTextures(assets, keys, assetId, scratch);
            return scratch.Count > 0;
        }, resolved, out int textures, out int unresolved);

        return CutsceneManifestMatch.IsUnmatched(textures, unresolved) && !AreModelImportsActive
            ? ECutsceneManifestState.Unmatched
            : ECutsceneManifestState.Current;
    }

    /// <summary>
    /// Adds the loaded textures of a manifest ID to a list. First it looks for a runtime ID in the loaded assets of the asset
    /// manager and then in the object cache, because a texture that a model import creates is not an asset of the manager.
    /// Then it looks for a stable key of <see cref="CutsceneResidencyAssetKeys"/>, because a model import makes new runtime
    /// IDs at each import. A texture that is destroyed or queued for destroy is not added.
    /// </summary>
    /// <param name="assets">The asset manager that holds the loaded texture assets.</param>
    /// <param name="keys">The index of the stable texture keys.</param>
    /// <param name="assetId">The ID that the manifest records for the texture.</param>
    /// <param name="destination">The list that receives the textures.</param>
    internal static void ResolveLoadedTextures(AssetManager assets, TextureResidencyKeyIndex keys, Guid assetId, List<XRTexture2D> destination)
    {
        if (assets.TryGetAssetByID(assetId, out XRAsset? asset))
        {
            if (asset is XRTexture2D stored && TextureResidencyKeyIndex.IsLive(stored))
                destination.Add(stored);
            return;
        }

        if (XRObjectBase.ObjectsCache.TryGetValue(assetId, out XRObjectBase? cached))
        {
            if (cached is XRTexture2D texture && TextureResidencyKeyIndex.IsLive(texture))
                destination.Add(texture);
            return;
        }

        keys.Find(assetId, destination);
    }

    private static Action<Guid, List<XRTexture2D>> AdaptResolver(Func<Guid, XRTexture2D?> resolveTexture)
    {
        ArgumentNullException.ThrowIfNull(resolveTexture);
        return (assetId, destination) =>
        {
            if (resolveTexture(assetId) is { } texture)
                destination.Add(texture);
        };
    }

    /// <summary>
    /// Resolves an asset one time for each selection, and keeps the result for later calls. A kept result whose textures
    /// are all dead (for example after a reimport while a preview is open) is resolved again, and the first such result of a
    /// selection lets the key index build one more time.
    /// </summary>
    private XRTexture2D[] ResolveCached(Guid assetId)
    {
        lock (_resolveSync)
        {
            if (_resolved.TryGetValue(assetId, out XRTexture2D[]? textures))
            {
                if (textures.Length == 0 || HasLive(textures))
                    return textures;

                _resolved.Remove(assetId);
                if (!_deadHitRebuildUsed)
                {
                    _deadHitRebuildUsed = true;
                    _keys?.AllowRebuild();
                }
            }

            _resolveScratch.Clear();
            _resolveTextures(assetId, _resolveScratch);
            textures = _resolveScratch.Count == 0 ? [] : [.. _resolveScratch];
            _resolveScratch.Clear();
            _resolved.Add(assetId, textures);
            return textures;
        }
    }

    private static bool HasLive(XRTexture2D[] textures)
    {
        for (int i = 0; i < textures.Length; i++)
            if (TextureResidencyKeyIndex.IsLive(textures[i]))
                return true;
        return false;
    }

    private void ClearResolved()
    {
        lock (_resolveSync)
            _resolved.Clear();
    }
}
