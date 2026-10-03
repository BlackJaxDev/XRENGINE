using System.Text.Json;
using XREngine.Core.Files;
using XREngine.Core;
using XREngine.Components.Scripting;
using XREngine.Diagnostics;
using XREngine.Editor.Publishing;
using XREngine.Publishing;
using XREngine.Scene;

namespace XREngine.Editor;

internal static partial class ProjectBuilder
{
    private sealed class BrowserBuildState(BuildContext context)
    {
        private readonly string _stageRoot = Path.Combine(Path.GetDirectoryName(context.BuildRoot)!,
            "." + Path.GetFileName(context.BuildRoot) + ".browser-stage-" + Guid.NewGuid().ToString("N"));
        private string? _siteRoot;
        private string? _recipePath;
        private string? _configuration;
        private bool _includesDefaultUiFont;
        private IReadOnlyList<BrowserUiFontCookRequest> _authoredFonts = [];
        private BrowserSharedWorldPackage? _sharedWorldPackage;

        private string SourceRoot => Path.Combine(_stageRoot, "content-source");
        private string PublishRoot => Path.Combine(_stageRoot, "publish");
        private static CancellationToken Cancellation => _activeJob?.CancellationToken ?? CancellationToken.None;

        internal void Prepare()
        {
            Cancellation.ThrowIfCancellationRequested();
            string buildDirectory = Path.GetFullPath(context.Project.BuildDirectory!);
            string relative = Path.GetRelativePath(buildDirectory, context.BuildRoot);
            if (Path.IsPathRooted(relative) || relative == "." || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Browser output must be a subdirectory of the project Build directory.");
            RejectBrowserOutputLinks(context.BuildRoot, buildDirectory);
            Directory.CreateDirectory(_stageRoot);
            Directory.CreateDirectory(SourceRoot);
        }

        internal void ExportAuthoredWorld()
        {
            using IDisposable diagnosticPreferences = XREngine.Rendering.RenderDiagnosticsFlags.SuppressDeferredDebugViewWritesForCurrentThread();
            try
            {
                Cancellation.ThrowIfCancellationRequested();
                var activeGame = GameCSProjLoader.GetLoadedAssembly("GAME")
                    ?? throw new InvalidOperationException("BrowserCook.GameAssemblyMissing: the browser-target game must be loaded before world export.");
                string gameName = activeGame.GetName().Name!;
                using IDisposable preferredGameTypes = AotRuntimeMetadataStore.PreferDevelopmentTypes((name, ignoreCase) =>
                {
                    string fullName = SerializedTypeIdentity.GetUnqualifiedTypeName(name);
                    if (name.Length > fullName.Length)
                    {
                        string qualifier = name[(fullName.Length + 1)..].Split(',')[0].Trim();
                        if (!string.Equals(qualifier, gameName, StringComparison.Ordinal))
                            return null;
                    }
                    return activeGame.GetType(fullName, throwOnError: false, ignoreCase: ignoreCase);
                });
                if (string.IsNullOrWhiteSpace(context.Project.BrowserSharedWorldPackageManifestPath))
                {
                    using XREngine.Data.Core.ObjectCachePublicationScope publication = XREngine.Data.Core.XRObjectBase.BeginIndependentObjectCachePublication();
                    XRWorld world = LoadStartupWorld(context);
                    _recipePath = CookBrowserEngineWorld(world, context.Project, context.AssetsDirectory, SourceRoot,
                        Cancellation, out _includesDefaultUiFont, out _authoredFonts);
                    using XREngine.Data.Core.ObjectCacheOwnership ownership = publication.CompleteWithOwnership();
                }
                else
                {
                    string worldPath = ResolveBrowserStartupWorldPath(context);
                    _sharedWorldPackage = PrepareBrowserSharedWorldPackage(context.Project, worldPath, context.AssetsDirectory, Cancellation);
                    using XREngine.Data.Core.ObjectCachePublicationScope publication = XREngine.Data.Core.XRObjectBase.BeginIndependentObjectCachePublication();
                    using StringReader reader = new(DecodeSharedNativeWorld(_sharedWorldPackage.NativeWorldBytes));
                    using var sourceContext = AssetDeserializationContext.Push(worldPath);
                    XRWorld world = AssetManager.Deserializer.Deserialize<XRWorld>(reader)
                        ?? throw new InvalidDataException("BrowserCook.SharedPackageWorldInvalid: retained native bytes did not hydrate an XRWorld.");
                    if (world.GetType() != typeof(XRWorld))
                        throw new NotSupportedException("BrowserCook.SharedPackageProfileUnsupported: requires the exact base XRWorld type.");
                    world.FilePath = worldPath;
                    _recipePath = CookBrowserEngineWorld(world, context.Project, context.AssetsDirectory, SourceRoot,
                        Cancellation, out _includesDefaultUiFont, out _authoredFonts);
                    RequireSharedBrowserCook(_sharedWorldPackage, _recipePath);
                    using XREngine.Data.Core.ObjectCacheOwnership ownership = publication.CompleteWithOwnership();
                }
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        internal void PublishApplication(string configuration)
        {
            try
            {
                _configuration = configuration;
                _siteRoot = global::CodeManager.Instance.PublishBrowserApplication(
                    configuration, PublishRoot, context.Settings.IncludePdbFiles, Cancellation);
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        internal void PackageContent()
        {
            try
            {
                string recipe = _recipePath ?? throw new InvalidOperationException("Browser world export did not produce a recipe.");
                string site = _siteRoot ?? throw new InvalidOperationException("Browser application has not been published.");
                string configuration = _configuration ?? throw new InvalidOperationException("Browser publish configuration was not recorded.");
                WriteBrowserRuntimeMetadata(configuration, SourceRoot, site);
                BrowserContentPackageBuilder.Build(recipe, Path.Combine(site, "content"), Cancellation);
                PublishBrowserSharedWorldPackage(_sharedWorldPackage, Path.Combine(site, "content"), Cancellation);
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        internal void WriteLaunchConfiguration()
        {
            try
            {
                Cancellation.ThrowIfCancellationRequested();
                string site = _siteRoot ?? throw new InvalidOperationException("Browser application has not been published.");
                InstallPlayerShell(site);
                if (_includesDefaultUiFont)
                    InstallDefaultUiFontLicense(site);
                InstallAuthoredFontLicenses(site, context.AssetsDirectory, _authoredFonts);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = 2,
                    format = "xrengine-engine-launch",
                    manifest = "./content/manifest.json",
                    quality = (string?)null
                });
                File.WriteAllBytes(Path.Combine(site, "browser-publish.json"), json);
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        private void InstallPlayerShell(string site)
        {
            string expectedSite = Path.GetFullPath(Path.Combine(PublishRoot, "wwwroot"));
            if (!string.Equals(Path.GetFullPath(site), expectedSite, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Browser publish returned an unexpected site directory.");
            DirectoryInfo siteDirectory = new(expectedSite);
            if (!siteDirectory.Exists || siteDirectory.LinkTarget is not null ||
                (siteDirectory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Browser publish site must be a regular directory.");

            string PlayerFile(string name)
            {
                string path = Path.GetFullPath(Path.Combine(expectedSite, name));
                if (!string.Equals(Path.GetDirectoryName(path), expectedSite, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Browser shell file resolved outside the published site.");
                FileInfo file = new(path);
                if (file.LinkTarget is not null || (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0))
                    throw new InvalidOperationException("Browser shell files cannot be linked.");
                return path;
            }

            string player = PlayerFile("engine-player.html");
            if (!File.Exists(player) || !File.Exists(PlayerFile("engine-player.js")) ||
                !File.Exists(PlayerFile("engine-runtime.js")))
                throw new InvalidOperationException("Browser publish did not include the player shell.");

            // Static hosts may prefer stale precompressed variants over the replaced HTML.
            foreach (string name in new[]
            {
                "index.html.br", "index.html.gz", "main.js", "main.js.br", "main.js.gz",
                "browser-publish.json.br", "browser-publish.json.gz"
            })
                File.Delete(PlayerFile(name));
            File.Copy(player, PlayerFile("index.html"), overwrite: true);
        }

        private static void InstallDefaultUiFontLicense(string site)
        {
            string source = Engine.Assets.ResolveEngineAssetPath("Fonts", "Roboto", "LICENSE.txt");
            FileInfo license = new(source);
            if (!license.Exists || license.LinkTarget is not null ||
                (license.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new FileNotFoundException("BrowserCook.DefaultUiFontLicenseMissing: canonical Roboto license is unavailable.", source);
            string licensesDirectory = Path.Combine(site, "licenses");
            DirectoryInfo destinationDirectory = new(licensesDirectory);
            if (destinationDirectory.Exists && (destinationDirectory.LinkTarget is not null ||
                (destinationDirectory.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new NotSupportedException("BrowserCook.DefaultUiFontLicenseLinked: license output cannot be linked.");
            Directory.CreateDirectory(licensesDirectory);
            string destination = Path.Combine(licensesDirectory, "Roboto-LICENSE.txt");
            FileInfo existing = new(destination);
            if (existing.Exists && (existing.LinkTarget is not null ||
                (existing.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new NotSupportedException("BrowserCook.DefaultUiFontLicenseLinked: license output cannot be linked.");
            File.Copy(source, destination, overwrite: true);
        }

        private static void InstallAuthoredFontLicenses(string site, string assetRoot,
            IReadOnlyList<BrowserUiFontCookRequest> fonts)
        {
            if (fonts.Count == 0)
                return;
            string directory = Path.Combine(site, "licenses");
            DirectoryInfo output = new(directory);
            if (output.Exists && (output.LinkTarget is not null ||
                (output.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new NotSupportedException("BrowserCook.FontNoticeOutputLinked: license directory cannot be linked.");
            Directory.CreateDirectory(directory);
            foreach (BrowserUiFontCookRequest font in fonts)
            {
                RequireBrowserRegularFile(font.NoticePath, assetRoot, 256 * 1024, "FontNotice");
                byte[] notice = File.ReadAllBytes(font.NoticePath);
                if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(notice)) != font.NoticeHash)
                    throw new InvalidDataException("BrowserCook.FontNoticeChanged: notice changed during publication.");
                string destination = Path.Combine(directory, font.NoticeOutputName);
                FileInfo existing = new(destination);
                if (existing.Exists && (existing.LinkTarget is not null ||
                    (existing.Attributes & FileAttributes.ReparsePoint) != 0))
                    throw new NotSupportedException("BrowserCook.FontNoticeOutputLinked: license output cannot be linked.");
                File.WriteAllBytes(destination, notice);
            }
        }

        internal void Commit()
        {
            Cancellation.ThrowIfCancellationRequested();
            string site = _siteRoot ?? throw new InvalidOperationException("Browser application has not been published.");
            if (!File.Exists(Path.Combine(site, "index.html")) ||
                !File.Exists(Path.Combine(site, "browser-publish.json")) ||
                !File.Exists(Path.Combine(site, "content", "manifest.json")) ||
                (_includesDefaultUiFont && !File.Exists(Path.Combine(site, "licenses", "Roboto-LICENSE.txt"))) ||
                _authoredFonts.Any(font => !File.Exists(Path.Combine(site, "licenses", font.NoticeOutputName))))
            {
                Cleanup();
                throw new InvalidOperationException("Browser output is incomplete; keeping the previous build.");
            }
            RejectBrowserOutputLinks(context.BuildRoot, Path.GetFullPath(context.Project.BuildDirectory!));
            string backup = context.BuildRoot + ".browser-backup-" + Guid.NewGuid().ToString("N");
            bool movedPrevious = false;
            try
            {
                if (Directory.Exists(context.BuildRoot))
                {
                    Directory.Move(context.BuildRoot, backup);
                    movedPrevious = true;
                }
                try
                {
                    Directory.Move(site, context.BuildRoot);
                }
                catch
                {
                    if (movedPrevious && !Directory.Exists(context.BuildRoot))
                        Directory.Move(backup, context.BuildRoot);
                    throw;
                }
            }
            finally
            {
                Cleanup();
            }
            if (movedPrevious)
            {
                try { Directory.Delete(backup, recursive: true); }
                catch (IOException error) { Debug.LogWarning($"Previous browser output remains at '{backup}': {error.Message}"); }
                catch (UnauthorizedAccessException error) { Debug.LogWarning($"Previous browser output remains at '{backup}': {error.Message}"); }
            }
        }

        internal void Cleanup()
        {
            if (!Directory.Exists(_stageRoot)) return;
            try { Directory.Delete(_stageRoot, recursive: true); }
            catch (IOException error) { Debug.LogWarning($"Browser staging remains at '{_stageRoot}': {error.Message}"); }
            catch (UnauthorizedAccessException error) { Debug.LogWarning($"Browser staging remains at '{_stageRoot}': {error.Message}"); }
        }
    }

    private static List<BuildStep> CreateBrowserSteps(BuildSettings settings, BuildContext context)
    {
        ValidateBrowserSettings(settings);
        BrowserBuildState state = new(context);
        context.CleanupAfterBuild = state.Cleanup;
        List<BuildStep> steps = [];
        if (settings.SaveSettingsBeforeBuild)
            steps.Add(new BuildStep("Saving project settings", Engine.SaveProjectSettings));
        steps.Add(new BuildStep("Preparing staged browser output", state.Prepare));
        steps.Add(new BuildStep("Compiling portable game assemblies", () =>
        {
            global::CodeManager manager = global::CodeManager.Instance;
            manager.RemakeSolutionAsDLL(false);
            manager.BuildBrowserGameAssemblyForPublishing(ResolveConfiguration(settings.Configuration),
                _activeJob?.CancellationToken ?? CancellationToken.None);
        }));
        steps.Add(new BuildStep("Cooking authored engine startup world", state.ExportAuthoredWorld));
        steps.Add(new BuildStep("Publishing WebAssembly browser application",
            () => state.PublishApplication(ResolveConfiguration(settings.Configuration))));
        steps.Add(new BuildStep("Packaging browser content", state.PackageContent));
        steps.Add(new BuildStep("Writing browser launch configuration", state.WriteLaunchConfiguration));
        steps.Add(new BuildStep("Activating browser output", state.Commit));
        return steps;
    }

    private static void ValidateBrowserSettings(BuildSettings settings)
    {
        if (!settings.CleanOutputDirectory)
            throw new InvalidOperationException("Browser builds replace the complete static bundle; enable CleanOutputDirectory.");
        if (!settings.CookContent || !settings.BuildManagedAssemblies || !settings.BuildLauncherExecutable)
            throw new InvalidOperationException("Browser builds require CookContent, BuildManagedAssemblies and BuildLauncherExecutable.");
        if (settings.PublishLauncherAsNativeAot || settings.ValidateLauncherAotCompatibility)
            throw new NotSupportedException("Browser builds use WebAssembly publish, not the Windows NativeAOT launcher flags.");
        if (!string.IsNullOrWhiteSpace(settings.LauncherDefineConstants))
            throw new NotSupportedException("Native launcher compile constants are unsupported for the browser target.");
        if (settings.RendererBackendPackage != ERendererBackendPackageMode.All)
            throw new NotSupportedException("Browser builds select the WebGPU module; desktop OpenGL/Vulkan backend selection is unsupported.");
    }

    private static XRWorld LoadStartupWorld(BuildContext context)
    {
        string path = ResolveBrowserStartupWorldPath(context);
        // Deserialize afresh from the authored asset; AssetManager.Load may return the editor's mutable cache.
        // The abstract base lets the saved type hint select a game-owned XRWorld subclass.
        XRWorld world = AssetManager.DeserializeAssetFile(path, typeof(XRAsset)) as XRWorld
            ?? throw new InvalidOperationException("Startup target asset did not deserialize as XRWorld.");
        world.FilePath = path;
        return world;
    }

    private static string ResolveBrowserStartupWorldPath(BuildContext context)
    {
        GameStartupSettings startup = Engine.PersistentGameSettings
            ?? throw new InvalidOperationException("Save game startup settings before browser publishing.");
        if (startup.RunWithoutWindows)
            throw new NotSupportedException("Browser publishing requires an authored startup window with a target world.");
        string? path = null;
        foreach (var window in startup.StartupWindows)
        {
            string candidate = window.TargetWorld?.FilePath ??
                throw new InvalidOperationException("Every browser startup window must select a saved target world.");
            candidate = Path.GetFullPath(candidate);
            if (path is not null && !string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Browser publishing supports one startup world; windows select different worlds.");
            path = candidate;
        }
        if (path is null)
        {
            string selectedWorld = context.Project.StartupScenePath;
            if (string.IsNullOrWhiteSpace(selectedWorld) || Path.IsPathRooted(selectedWorld) ||
                Uri.TryCreate(selectedWorld, UriKind.Absolute, out _))
                throw new InvalidOperationException("Select and save a startup window target world or project StartupScenePath before browser publishing.");
            path = Path.GetFullPath(Path.Combine(context.AssetsDirectory, selectedWorld));
        }
        string assetRoot = Path.GetFullPath(context.AssetsDirectory);
        string relative = Path.GetRelativePath(assetRoot, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(path), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Browser startup world must be a saved project .asset inside Assets.");
        if (!File.Exists(path)) throw new FileNotFoundException("Saved browser startup world was not found.", path);
        return path;
    }

    private static void RejectBrowserOutputLinks(string output, string buildDirectory)
    {
        for (string? path = output; path is not null; path = Path.GetDirectoryName(path))
        {
            DirectoryInfo directory = new(path);
            if (directory.LinkTarget is not null)
                throw new NotSupportedException("Browser output cannot replace a linked directory.");
            if (File.Exists(path) || Directory.Exists(path))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new NotSupportedException("Browser output cannot replace a linked directory.");
            }
            if (string.Equals(path, buildDirectory, StringComparison.OrdinalIgnoreCase)) break;
        }
    }
}
