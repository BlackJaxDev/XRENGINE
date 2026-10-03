using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using MemoryPack;
using XREngine.Components.Scripting;
using XREngine.Core.Files;
using XREngine.Publishing;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;

namespace XREngine.Editor;

internal static partial class ProjectBuilder
{
    /// <summary>Publishes type metadata for the compiled browser assembly closure and linked game.</summary>
    private static void WriteBrowserRuntimeMetadata(string configuration, string intermediateDirectory,
        string sourceDirectory, string siteDirectory)
    {
        string browserProject = global::CodeManager.ResolveBrowserProject();
        string browserDirectory = Path.GetDirectoryName(browserProject)!;
        string publisherRoot = Path.GetDirectoryName(browserDirectory)!;
        string closurePath = Path.Combine(publisherRoot, "Build", "Portable", "PortableProjects.tsv");
        if (!File.Exists(closurePath))
            throw new FileNotFoundException("BrowserPublish.PortableClosureMissing: the reviewed portable project inventory is required.", closurePath);

        string binaryDirectory = global::CodeManager.ResolveBrowserAssemblyDirectory(configuration, intermediateDirectory);
        if (!File.Exists(Path.Combine(binaryDirectory, "XREngine.Browser.dll")))
            throw new FileNotFoundException("BrowserPublish.AssemblyClosureMissing: the published browser managed output is required.",
                Path.Combine(binaryDirectory, "XREngine.Browser.dll"));

        HashSet<string> allowed = [.. File.ReadLines(closurePath)
            .Select(static line => line.Trim())
            .Where(static line => line.Length != 0 && !line.StartsWith('#'))];
        string frameworkDirectory = Path.Combine(siteDirectory, "_framework");
        if (!Directory.Exists(frameworkDirectory))
            throw new DirectoryNotFoundException("BrowserPublish.ManagedClosureMissing: the published framework directory is absent.");
        HashSet<string> shippedNames = [.. Directory.EnumerateFiles(frameworkDirectory, "*.wasm", SearchOption.TopDirectoryOnly)
            .Select(FrameworkAssemblyName)
            .Where(static name => name is not null)
            .Cast<string>()];
        string[] publishedNames = [.. shippedNames.Where(allowed.Contains).OrderBy(static name => name, StringComparer.Ordinal)];
        if (!publishedNames.Contains("XREngine.Browser", StringComparer.Ordinal)
            || !publishedNames.Contains("XREngine.Runtime.Core", StringComparer.Ordinal))
            throw new InvalidDataException("BrowserPublish.AssemblyClosureIncomplete: required browser and core assemblies are absent.");

        Assembly game = GameCSProjLoader.GetLoadedAssembly("GAME")
            ?? throw new InvalidOperationException("BrowserPublish.GameAssemblyMissing: the compiled game is not loaded for cooking.");
        string builtGamePath = global::CodeManager.Instance.GetBrowserGameAssemblyPath(configuration);
        if (game.ManifestModule.ModuleVersionId != ReadModuleVersionId(builtGamePath))
            throw new InvalidOperationException("BrowserPublish.GameAssemblyStale: the loaded game differs from the compiled game binary; rebuild before publishing.");
        string browserGamePath = Path.Combine(binaryDirectory, game.GetName().Name + ".dll");
        if (!File.Exists(browserGamePath) || game.ManifestModule.ModuleVersionId != ReadModuleVersionId(browserGamePath))
            throw new InvalidOperationException("BrowserPublish.LinkedGameStale: the published browser game differs from the cooked game assembly.");
        if (game.GetName().Name is not { } gameName || !shippedNames.Contains(gameName))
            throw new InvalidDataException("BrowserPublish.LinkedGameMissing: the authored game assembly is absent from the browser bundle.");
        using BrowserMetadataLoadContext browserContext = new(binaryDirectory, shippedNames);
        List<Assembly> assemblies = [.. publishedNames.Select(browserContext.LoadPublishedAssembly)];
        assemblies.Add(browserContext.LoadPublishedAssembly(gameName));
        using IDisposable registrations = RuntimeAssetBootstrap.InstallEngineAssetServices();
        ValidateCookedBrowserCodecOwnership(sourceDirectory, game);
        AotRuntimeMetadata metadata = AotRuntimeMetadataBuilder.BuildBrowser(assemblies, game, [typeof(FontGlyphSet)]);
        File.WriteAllBytes(Path.Combine(sourceDirectory, AotRuntimeMetadataStore.MetadataFileName),
            MemoryPackSerializer.Serialize(metadata));
    }

    private static void ValidateCookedBrowserCodecOwnership(string sourceDirectory, Assembly activeGame)
    {
        string recipePath = Path.Combine(sourceDirectory, "engine-assets.recipe.json");
        using JsonDocument recipe = JsonDocument.Parse(File.ReadAllBytes(recipePath));
        Type[] registered = [.. PublishedCookedAssetRegistry.SnapshotRegisteredTypes(), typeof(FontGlyphSet)];
        string? gameName = activeGame.GetName().Name;
        foreach (JsonElement asset in recipe.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("encoding").GetString() != "cooked-binary"
                || asset.GetProperty("path").GetString() == "/engine/Metadata/AotRuntimeMetadata.bin")
                continue;
            string sourceName = asset.GetProperty("source").GetString()!;
            if (sourceName != Path.GetFileName(sourceName))
                throw new InvalidDataException("BrowserPublish.CookedAssetSourceInvalid: a cooked asset source must be a file name.");
            CookedAssetBlob blob = MemoryPackSerializer.Deserialize<CookedAssetBlob>(
                File.ReadAllBytes(Path.Combine(sourceDirectory, sourceName)));
            if (blob.Format != CookedAssetFormat.RuntimeBinaryV1)
                continue;
            string typeName = asset.GetProperty("type").GetString()!;
            if (!registered.Any(type => type.AssemblyQualifiedName == typeName
                && (type.Assembly.GetName().Name != gameName || type.Assembly == activeGame)))
                throw new InvalidDataException($"BrowserPublish.CookedCodecStale: '{typeName}' is not registered by the active game or shared browser codec owners.");
        }
    }

    private static Guid ReadModuleVersionId(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader image = new(stream);
        MetadataReader reader = image.GetMetadataReader();
        return reader.GetGuid(reader.GetModuleDefinition().Mvid);
    }

    private static string? FrameworkAssemblyName(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        int separator = stem.LastIndexOf('.');
        return separator > 0 && stem.Length - separator - 1 == 10
            && stem.AsSpan(separator + 1).ToString().All(static character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9')
            ? stem[..separator] : null;
    }

}
