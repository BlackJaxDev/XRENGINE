using System.Text.Json;
using XREngine.Scene;
using XREngine.Core.Files;
using XREngine.Editor.Publishing;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Editor;

internal static partial class ProjectBuilder
{
    /// <summary>Cooks the authored engine world using the same registered format as desktop publishing.</summary>
    private static string CookBrowserEngineWorld(XRWorld world, string assetRoot, string sourceDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(sourceDirectory);
        // The existing serializer owns component graphs and game-specific formats. No
        // browser scene projection is allowed to remove authored gameplay behavior.
        IShaderProgramArtifactResolver? resolver = Engine.CurrentProject is { ProjectDirectory: { } projectDirectory, BrowserShaderArtifactManifestPath: { Length: > 0 } manifest }
            ? new BrowserShaderArtifactSource(projectDirectory, manifest) : null;
        IReadOnlyList<ShaderProgramArtifact> shaderArtifacts = BrowserWorldCapabilityAudit.Inspect(world, resolver, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string relativeWorld = Path.GetRelativePath(assetRoot, world.FilePath!).Replace('\\', '/');
        string worldPath = "/game/" + relativeWorld;
        if (string.Equals(worldPath, "/game/startup.asset", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Browser startup world conflicts with the cooked startup-settings identity.");
        BrowserAssetDependencyCooker dependencyCooker = new(
            assetRoot, Engine.Assets?.EngineAssetsPath, sourceDirectory, cancellationToken);
        dependencyCooker.Cook(world, worldPath, "startup-world.bin");
        GameStartupSettings settings = Engine.PersistentGameSettings.DeepClone();
        // Keep authored output requirements while the manifest owns the selected world.
        // Copy window objects so removing external world references cannot mutate editor settings.
        settings.StartupWindows = settings.StartupWindows.Select(window => new GameWindowStartupSettings
        {
            Width = window.Width, Height = window.Height, WindowTitle = window.WindowTitle,
            LocalPlayers = window.LocalPlayers, WindowState = window.WindowState, X = window.X, Y = window.Y,
            VSync = window.VSync, TransparentFramebuffer = window.TransparentFramebuffer, OutputHDR = window.OutputHDR,
            UseNativeTitleBar = window.UseNativeTitleBar, InteractiveResizeStrategy = window.InteractiveResizeStrategy,
            TargetWorld = null
        }).ToList();
        settings.RunWithoutWindows = true;
        settings.LogOutputToFile = false;
        dependencyCooker.Cook(settings, "/game/startup.asset", "startup-settings.bin");
        List<object> assets = [.. dependencyCooker.Entries
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(static entry => (object)new { path = entry.Key, type = entry.Value.TypeName, encoding = "cooked-binary",
                source = entry.Value.Source, dependencies = entry.Value.Dependencies })];
        List<object> shaderReferences = [];
        HashSet<string> shaderIdentities = new(StringComparer.Ordinal);
        foreach (ShaderProgramArtifact artifact in shaderArtifacts)
        {
            shaderIdentities.Add(artifact.Identity);
            string descriptorName = artifact.Identity + ".json";
            string sourceName = artifact.Identity + ".wgsl";
            File.WriteAllBytes(Path.Combine(sourceDirectory, descriptorName), artifact.DescriptorBytes.ToArray());
            File.WriteAllBytes(Path.Combine(sourceDirectory, sourceName), artifact.Artifact.Bytes);
            string descriptor = "/engine/Shaders/Cooked/" + descriptorName;
            string source = "/engine/Shaders/Cooked/" + sourceName;
            string textType = typeof(TextFile).AssemblyQualifiedName!;
            assets.Add(new { path = descriptor, type = textType, encoding = "utf8-text", source = descriptorName, dependencies = Array.Empty<string>() });
            assets.Add(new { path = source, type = textType, encoding = "utf8-text", source = sourceName, dependencies = Array.Empty<string>() });
            shaderReferences.Add(new { identity = artifact.Identity, descriptor, source });
        }
        List<object> materialVariants = [];
        if (resolver is BrowserShaderArtifactSource shaderSource)
        {
            foreach (EngineMaterialVariantEntry variant in shaderSource.MaterialVariants)
            {
                if (!shaderIdentities.Contains(variant.DescriptorIdentity))
                    continue;
                materialVariants.Add(new
                {
                    semantic = variant.Key.Semantic.Semantic.ToString(),
                    semanticVersion = variant.Key.Semantic.Version,
                    target = variant.Key.Target.ToString(),
                    pass = variant.Key.Pass,
                    vertexProfile = variant.Key.VertexProfile,
                    outputProfile = variant.Key.OutputProfile,
                    descriptorIdentity = variant.DescriptorIdentity
                });
            }
        }
        byte[] recipe = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, format = "xrengine-assets", startupWorld = worldPath,
            startupSettings = "/game/startup.asset", shaderArtifacts = shaderReferences, materialVariants, assets
        });
        string path = Path.Combine(sourceDirectory, "engine-assets.recipe.json");
        File.WriteAllBytes(path, recipe);
        return path;
    }
}
