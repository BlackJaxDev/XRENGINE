using XREngine.Components.Scene.Volumes;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor.Publishing;
using XREngine.Rendering;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Scene;

namespace XREngine.Editor;

internal static partial class ProjectBuilder
{
    /// <summary>Cooks explicitly authored scene roots through the same asset graph as the startup world.</summary>
    private static (string[] Roots, bool IncludesDefaultFont, IReadOnlyList<BrowserUiFontCookRequest> Fonts,
        IReadOnlyList<ShaderProgramArtifact> ShaderArtifacts)
        CookBrowserStreamedScenes(XRProject project, XRWorld startupWorld, string assetRoot, string sourceDirectory,
            BrowserAssetDependencyCooker dependencyCooker, IShaderProgramArtifactResolver? resolver,
            HashSet<string> cookedFonts, CancellationToken cancellationToken, RenderPipelineResourceProfile outputProfile,
            HashSet<int> admittedScenePasses)
    {
        SortedSet<string> pending = new(StringComparer.Ordinal);
        foreach (string path in project.BrowserStreamedScenePaths ?? [])
            pending.Add(path);
        foreach (string path in BrowserStreamedSceneReferences(startupWorld.Scenes))
            pending.Add(path);

        HashSet<string> roots = new(StringComparer.Ordinal);
        Dictionary<string, string[]> sceneTargets = new(StringComparer.Ordinal);
        Dictionary<string, BrowserUiFontCookRequest> selectedFonts = new(StringComparer.Ordinal);
        Dictionary<string, ShaderProgramArtifact> shaderArtifacts = new(StringComparer.Ordinal);
        bool requiresDefaultFont = false;
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string declaredPath = pending.Min!;
            pending.Remove(declaredPath);
            (string identity, string filePath) = dependencyCooker.ResolveStreamedScene(declaredPath);
            if (!roots.Add(identity))
                continue;
            if (roots.Count > 256)
                throw new InvalidDataException("BrowserCook.StreamedSceneBudgetExceeded: at most 256 scene roots may be declared.");
            using ObjectCachePublicationScope authoredPublication = XRObjectBase.BeginIndependentObjectCachePublication();
            XRScene scene = AssetManager.DeserializeAssetFile(filePath, typeof(XRAsset)) as XRScene
                ?? throw new InvalidDataException($"BrowserCook.StreamedSceneInvalid: '{identity}' did not deserialize as an XRScene.");
            scene.FilePath = filePath;
            if (!scene.IsVisible)
                throw new NotSupportedException($"BrowserCook.StreamedSceneHidden: '{identity}' must be authored visible for browser volume attachment.");
            requiresDefaultFont |= PrepareBrowserUiFonts([scene], assetRoot, out IReadOnlyList<BrowserUiFontCookRequest> sceneFonts);
            string sourceName;
            if (dependencyCooker.Entries.ContainsKey(identity))
                sourceName = dependencyCooker.RecookDeclaredScene(scene, identity);
            else
            {
                sourceName = $"streamed-scene-{roots.Count:D4}.bin";
                dependencyCooker.Cook(scene, identity, sourceName);
            }
            foreach (BrowserUiFontCookRequest font in sceneFonts)
            {
                selectedFonts.TryAdd(font.CatalogPath, font);
                if (cookedFonts.Add(font.CatalogPath))
                    CookBrowserUiFont(font, assetRoot, sourceDirectory, cancellationToken);
                dependencyCooker.AddCookedLeaf(identity, font.CatalogPath, typeof(FontGlyphSet), font.SourceName);
            }

            // Audit the actual runtime graph, not only the editor-authored scene. This
            // also catches lost stream targets and generated font references at cook time.
            using IDisposable wrapperSuppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
            using IDisposable materialTarget = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
            using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
            XRScene runtimeScene = CookedAssetReader.LoadAsset(File.ReadAllBytes(Path.Combine(sourceDirectory, sourceName)), typeof(XRScene)) as XRScene
                ?? throw new InvalidDataException($"BrowserCook.StreamedSceneInvalid: '{identity}' did not hydrate an XRScene.");
            VerifyPublishedUiFontReferences([scene], [runtimeScene]);
            string[] authoredBindings = BrowserStreamedSceneBindings([scene]);
            string[] cookedBindings = BrowserStreamedSceneBindings([runtimeScene]);
            if (!authoredBindings.SequenceEqual(cookedBindings, StringComparer.Ordinal))
                throw new InvalidDataException($"BrowserCook.StreamedSceneReferenceLost: '{identity}' changed its authored scene targets.");
            XRWorld auditWorld = new(identity, runtimeScene);
            foreach (ShaderProgramArtifact artifact in BrowserWorldCapabilityAudit.Inspect(auditWorld, resolver,
                cancellationToken, outputProfile, admittedScenePasses, admittedScenePasses))
                shaderArtifacts.TryAdd(artifact.Identity, artifact);
            using ObjectCacheOwnership ownership = publication.CompleteWithOwnership();
            using ObjectCacheOwnership authoredOwnership = authoredPublication.CompleteWithOwnership();
            string[] nestedPaths = BrowserStreamedSceneReferences([scene]);
            sceneTargets.Add(identity, [.. nestedPaths.Select(path => dependencyCooker.ResolveStreamedScene(path).Identity)
                .Distinct(StringComparer.Ordinal)]);
            foreach (string nestedPath in nestedPaths)
                pending.Add(nestedPath);
        }

        HashSet<string> visiting = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        foreach (string root in roots)
            ValidateStreamTargets(root, 0);

        return ([.. roots.Order(StringComparer.Ordinal)], requiresDefaultFont,
            [.. selectedFonts.Values.OrderBy(static font => font.CatalogPath, StringComparer.Ordinal)],
            [.. shaderArtifacts.Values.OrderBy(static artifact => artifact.Identity, StringComparer.Ordinal)]);

        void ValidateStreamTargets(string path, int depth)
        {
            if (visited.Contains(path))
                return;
            if (depth >= 32 || !visiting.Add(path))
                throw new InvalidDataException($"BrowserCook.StreamedSceneCycle: '{path}' forms a cycle or exceeds 32 nested scenes.");
            foreach (string target in sceneTargets[path])
                ValidateStreamTargets(target, depth + 1);
            visiting.Remove(path);
            visited.Add(path);
        }
    }

    private static string[] BrowserStreamedSceneReferences(IEnumerable<XRScene> scenes)
        => [.. BrowserStreamedSceneBindings(scenes)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string[] BrowserStreamedSceneBindings(IEnumerable<XRScene> scenes)
    {
        List<string> paths = [];
        HashSet<SceneNode> visited = new(ReferenceEqualityComparer.Instance);
        foreach (XRScene scene in scenes)
            foreach (SceneNode root in scene.RootNodes)
                Visit(root, 0);
        return [.. paths];

        void Visit(SceneNode node, int depth)
        {
            if (depth >= 128 || !visited.Add(node) || visited.Count > 8192)
                throw new InvalidDataException("BrowserCook.StreamedSceneGraphInvalid: scene nodes exceed the hierarchy budget or are shared.");
            foreach (var component in node.Components)
                if (component is SceneStreamingVolumeComponent volume)
                    paths.Add(volume.SceneAssetPath ?? string.Empty);
            foreach (var child in node.Transform.Children)
                if (child.SceneNode is SceneNode childNode)
                    Visit(childNode, depth + 1);
        }
    }
}
