using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Browser;

public sealed partial class BrowserEngineAssetSource : IRuntimeScenePreparationSource
{
    /// <summary>Resolves authored font identities from the verified asset graph before world activation.</summary>
    internal Task BindUiFontsAsync(XRWorld world, CancellationToken cancellationToken)
        => BindUiFontsAsync(world.Scenes, StartupWorldPath, cancellationToken);

    /// <summary>Resolves only resources declared by this scene's catalog dependency entry.</summary>
    public Task PrepareSceneAsync(XRScene scene, string catalogPath, CancellationToken cancellationToken = default)
        => BindUiFontsAsync([scene], catalogPath, cancellationToken);

    private async Task BindUiFontsAsync(IEnumerable<XRScene> scenes, string ownerPath, CancellationToken cancellationToken)
    {
        int session = RequireSession();
        if (!_assets.TryGetValue(ownerPath, out RuntimeAssetCatalogEntry? owner))
            throw new InvalidDataException($"AssetSource.SceneMissing: '{ownerPath}'.");
        Dictionary<string, FontGlyphSet> loaded = new(StringComparer.Ordinal);
        HashSet<SceneNode> visited = new(ReferenceEqualityComparer.Instance);
        foreach (XRScene scene in scenes)
            foreach (SceneNode root in scene.RootNodes)
                await Visit(root, 0);

        async Task Visit(SceneNode node, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session != RequireSession())
                throw new OperationCanceledException("AssetSource.StaleSession.");
            if (depth >= 128 || !visited.Add(node) || visited.Count > 8192)
                throw new InvalidDataException("AssetSource.UiFontSceneGraphInvalid.");
            foreach (var component in node.Components)
            {
                if (component is not UITextComponent { PublishedFontAssetPath: { } path } text)
                    continue;
                if (!path.StartsWith("/game/Fonts/Cooked/", StringComparison.Ordinal)
                    || !path.EndsWith(".cooked.asset", StringComparison.Ordinal)
                    || !owner.Dependencies.Contains(path)
                    || !_assets.TryGetValue(path, out RuntimeAssetCatalogEntry? entry)
                    || entry.Encoding != RuntimeAssetEncoding.CookedBinary
                    || entry.TypeName != typeof(FontGlyphSet).AssemblyQualifiedName
                    || entry.Dependencies.Count != 0)
                    throw new InvalidDataException($"AssetSource.UiFontDependencyInvalid: '{path}'.");
                if (!loaded.TryGetValue(path, out FontGlyphSet? font))
                {
                    font = await Engine.Assets.LoadFromRuntimeSourceAsync(path,
                        typeof(FontGlyphSet), cancellationToken: cancellationToken) as FontGlyphSet
                        ?? throw new InvalidDataException($"AssetSource.UiFontInvalid: '{path}' did not hydrate.");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (session != RequireSession())
                        throw new OperationCanceledException("AssetSource.StaleSession.");
                    if (font.AtlasType != EFontAtlasType.Bitmap || font.Glyphs is not { Count: > 0 }
                        || font.Atlas is not { Mipmaps.Length: > 0 })
                        throw new NotSupportedException($"AssetSource.UiFontUnsupported: '{path}' has no bitmap atlas.");
                    loaded.Add(path, font);
                }
                text.Font = font;
            }
            foreach (var child in node.Transform.Children)
                if (child.SceneNode is SceneNode childNode)
                    await Visit(childNode, depth + 1);
        }
    }
}
