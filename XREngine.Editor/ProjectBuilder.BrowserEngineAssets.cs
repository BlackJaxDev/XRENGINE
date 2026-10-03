using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Scene;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor.Publishing;
using XREngine.Rendering;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Editor;

internal static partial class ProjectBuilder
{
    /// <summary>Cooks the authored engine world using the same registered format as desktop publishing.</summary>
    private static string CookBrowserEngineWorld(XRWorld world, XRProject project, string assetRoot, string sourceDirectory,
        CancellationToken cancellationToken, out bool includesDefaultUiFont,
        out IReadOnlyList<BrowserUiFontCookRequest> authoredFonts)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string relativeWorld = Path.GetRelativePath(assetRoot, world.FilePath!).Replace('\\', '/');
        string worldPath = "/game/" + relativeWorld;
        using BrowserCapabilityReport capabilityReport = new(worldPath, project.IntermediateDirectory!, cancellationToken);
        capabilityReport.Save(complete: false);
        GameStartupSettings settings = Engine.PersistentGameSettings.DeepClone();
        BrowserRenderingCapabilityAudit.InspectStartup(settings, capabilityReport);
        RenderPipelineResourceProfile outputProfile = BrowserRenderPipelineOutputProfile.FromStartup(settings);
        Directory.CreateDirectory(sourceDirectory);
        // The existing serializer owns component graphs and game-specific formats. No
        // browser scene projection is allowed to remove authored gameplay behavior.
        IShaderProgramArtifactResolver? resolver = Engine.CurrentProject is { ProjectDirectory: { } projectDirectory, BrowserShaderArtifactManifestPath: { Length: > 0 } manifest }
            ? new BrowserShaderArtifactSource(projectDirectory, manifest) : null;
        includesDefaultUiFont = PrepareBrowserUiFonts(world.Scenes, assetRoot, out authoredFonts);
        if (string.Equals(worldPath, "/game/startup.asset", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Browser startup world conflicts with the cooked startup-settings identity.");
        using BrowserAssetDependencyCooker dependencyCooker = new(
            assetRoot, Engine.Assets?.EngineAssetsPath, sourceDirectory, resolver, cancellationToken);
        dependencyCooker.Cook(world, worldPath, "startup-world.bin");
        HashSet<string> cookedFonts = new(StringComparer.Ordinal);
        foreach (BrowserUiFontCookRequest font in authoredFonts)
        {
            CookBrowserUiFont(font, assetRoot, sourceDirectory, cancellationToken);
            cookedFonts.Add(font.CatalogPath);
            dependencyCooker.AddCookedLeaf(worldPath, font.CatalogPath, typeof(FontGlyphSet), font.SourceName);
        }
        Dictionary<string, ShaderProgramArtifact> shaderArtifacts = new(StringComparer.Ordinal);
        HashSet<int> admittedScenePasses = [];
        List<RenderPipelineRequirements> admittedPipelineRequirements = [];
        BrowserNativeSceneCapabilityAudit nativeAdmission = new();
        // Inspect what the browser will actually hydrate. A registered game serializer may
        // intentionally project desktop shader data into explicit cooked material semantics.
        // This CPU-only audit may run in a live desktop editor; temporary materials must not
        // attach API wrappers to its active renderer.
        {
            using IDisposable wrapperSuppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
            using IDisposable materialTarget = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
            using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
            XRWorld runtimeWorld = CookedAssetReader.LoadAsset(
                File.ReadAllBytes(Path.Combine(sourceDirectory, "startup-world.bin")), typeof(XRWorld),
                requireReferenceGraph: true) as XRWorld
                ?? throw new InvalidDataException("The browser startup payload did not hydrate an XRWorld.");
            VerifyPublishedUiFontReferences(world.Scenes, runtimeWorld.Scenes);
            if (!BrowserStreamedSceneBindings(world.Scenes).SequenceEqual(
                BrowserStreamedSceneBindings(runtimeWorld.Scenes), StringComparer.Ordinal))
                throw new InvalidDataException("BrowserCook.StreamedSceneReferenceLost: the cooked startup world changed its authored scene targets.");
            foreach (ShaderProgramArtifact artifact in BrowserWorldCapabilityAudit.Inspect(runtimeWorld, resolver,
                cancellationToken, outputProfile, admittedScenePasses: admittedScenePasses,
                report: capabilityReport, admittedPipelineRequirements: admittedPipelineRequirements, nativeAdmission: nativeAdmission))
                shaderArtifacts.TryAdd(artifact.Identity, artifact);
            // Keep authored IDs intact during inspection; publishing beside the
            // source world would rekey collisions before per-pipeline state lookup.
            using ObjectCacheOwnership ownership = publication.CompleteWithOwnership();
        }
        (string[] streamedRoots, bool streamedUsesDefaultFont, IReadOnlyList<BrowserUiFontCookRequest> streamedFonts,
            IReadOnlyList<ShaderProgramArtifact> streamedShaders) =
            CookBrowserStreamedScenes(project, world, assetRoot, sourceDirectory, dependencyCooker, resolver,
                cookedFonts, cancellationToken, outputProfile, admittedScenePasses, capabilityReport, admittedPipelineRequirements, nativeAdmission);
        nativeAdmission.Complete(capabilityReport, cancellationToken);
        string capabilityReportPath = capabilityReport.Save();
        capabilityReport.ThrowIfBlocked(capabilityReportPath);
        includesDefaultUiFont |= streamedUsesDefaultFont;
        authoredFonts = [.. authoredFonts.Concat(streamedFonts).DistinctBy(static font => font.CatalogPath)
            .OrderBy(static font => font.CatalogPath, StringComparer.Ordinal)];
        foreach (ShaderProgramArtifact artifact in streamedShaders)
            shaderArtifacts.TryAdd(artifact.Identity, artifact);
        cancellationToken.ThrowIfCancellationRequested();
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
        ClearBrowserRealtimeCredentials(settings);
        dependencyCooker.Cook(settings, "/game/startup.asset", "startup-settings.bin");
        List<object> assets = [.. dependencyCooker.Entries
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(static entry => (object)new { path = entry.Key, type = entry.Value.TypeName, encoding = "cooked-binary",
                source = entry.Value.Source, dependencies = entry.Value.Dependencies })];
        const string runtimeMetadataPath = "/engine/Metadata/AotRuntimeMetadata.bin";
        assets.Add(new { path = runtimeMetadataPath, type = typeof(AotRuntimeMetadata).AssemblyQualifiedName!,
            encoding = "cooked-binary", source = AotRuntimeMetadataStore.MetadataFileName,
            dependencies = Array.Empty<string>() });
        if (includesDefaultUiFont)
        {
            CookBrowserDefaultUiFont(sourceDirectory, cancellationToken);
            assets.Add(new { path = BrowserDefaultUiFontPath, type = typeof(FontGlyphSet).AssemblyQualifiedName!,
                encoding = "cooked-binary", source = "default-ui-font.bin", dependencies = Array.Empty<string>() });
        }
        List<object> shaderReferences = [];
        HashSet<string> shaderIdentities = new(StringComparer.Ordinal);
        List<string> essentialRoots = [worldPath, "/game/startup.asset", runtimeMetadataPath];
        if (includesDefaultUiFont)
            essentialRoots.Add(BrowserDefaultUiFontPath);
        foreach (ShaderProgramArtifact artifact in shaderArtifacts.Values.OrderBy(static artifact => artifact.Identity, StringComparer.Ordinal))
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
            essentialRoots.Add(descriptor);
            essentialRoots.Add(source);
            shaderReferences.Add(new { identity = artifact.Identity, descriptor, source });
        }
        List<object> materialVariants = [];
        List<object> pipelineArtifacts = [];
        List<object> computeArtifacts = [];
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
            foreach ((string bindingKey, string identity) in shaderSource.PipelineArtifacts)
            {
                if (!shaderIdentities.Contains(identity))
                    throw new InvalidDataException($"The declared browser pipeline artifact '{bindingKey}' was not packaged.");
                (string? scope, string pass) = WebPipelineArtifactCatalog.SplitBindingKey(bindingKey);
                if (scope is null)
                    pipelineArtifacts.Add(new { pass, descriptorIdentity = identity });
                else
                    pipelineArtifacts.Add(new { scope, pass, descriptorIdentity = identity });
            }
            foreach ((string kernel, string identity) in shaderSource.ComputeArtifacts)
            {
                if (!shaderIdentities.Contains(identity))
                    throw new InvalidDataException($"The declared browser compute artifact '{kernel}' was not packaged.");
                computeArtifacts.Add(new { kernel, descriptorIdentity = identity });
            }
        }
        byte[] recipe = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, format = "xrengine-assets", startupWorld = worldPath,
            startupSettings = "/game/startup.asset", publishedMetadata = runtimeMetadataPath, shaderArtifacts = shaderReferences,
            defaultUiFont = includesDefaultUiFont ? BrowserDefaultUiFontPath : null,
            essentialRoots = essentialRoots.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            streamedRoots, materialVariants, pipelineArtifacts, computeArtifacts, assets
        }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        string path = Path.Combine(sourceDirectory, "engine-assets.recipe.json");
        File.WriteAllBytes(path, recipe);
        return path;
    }

    /// <summary>Published settings must never carry an editor session's transient admission grant.</summary>
    private static void ClearBrowserRealtimeCredentials(GameStartupSettings settings)
    {
        settings.MultiplayerSessionId = null;
        settings.MultiplayerSessionToken = null;
        settings.MultiplayerAccountId = null;
        settings.MultiplayerReservationId = null;
        settings.MultiplayerAdmissionSecret = null;
        settings.MultiplayerClientId = null;
        settings.MultiplayerWorkerGeneration = null;
        settings.MultiplayerResumeRequested = false;
        settings.MultiplayerCredentialEpoch = 0;
        settings.ExpectedMultiplayerProtocolVersion = null;
        settings.ExpectedMultiplayerWorldAsset = null;
        settings.IgnoreEnvironmentRealtimeHandoffs = true;
    }
}
