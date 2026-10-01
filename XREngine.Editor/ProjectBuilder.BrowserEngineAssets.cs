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
        WriteCookedAsset(world, Path.Combine(sourceDirectory, "startup-world.bin"));
        cancellationToken.ThrowIfCancellationRequested();
        string type = world.GetType().AssemblyQualifiedName
            ?? throw new InvalidOperationException("The startup world has no stable runtime type identity.");
        string relativeWorld = Path.GetRelativePath(assetRoot, world.FilePath!).Replace('\\', '/');
        string worldPath = "/game/" + relativeWorld;
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
        WriteCookedAsset(settings, Path.Combine(sourceDirectory, "startup-settings.bin"));
        List<object> assets =
        [
            new { path = worldPath, type, encoding = "cooked-binary", source = "startup-world.bin", dependencies = Array.Empty<string>() },
            new { path = "/game/startup.asset", type = typeof(GameStartupSettings).AssemblyQualifiedName!, encoding = "cooked-binary",
                source = "startup-settings.bin", dependencies = Array.Empty<string>() }
        ];
        List<object> shaderReferences = [];
        foreach (ShaderProgramArtifact artifact in shaderArtifacts)
        {
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
        byte[] recipe = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, format = "xrengine-assets", startupWorld = worldPath,
            startupSettings = "/game/startup.asset", shaderArtifacts = shaderReferences, assets
        });
        string path = Path.Combine(sourceDirectory, "engine-assets.recipe.json");
        File.WriteAllBytes(path, recipe);
        return path;
    }
}
