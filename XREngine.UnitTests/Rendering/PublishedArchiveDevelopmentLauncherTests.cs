using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.Models;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// A non-AOT development launcher that reads the published archives loads a split cooked model from the content archive,
/// and its separate <c>lod/</c> entries resolve through <see cref="PublishedArchiveRegistry"/>.
/// </summary>
[TestFixture]
public sealed class PublishedArchiveDevelopmentLauncherTests
{
    private const string ModelEntry = "Models/Split.asset";

    private string _root = string.Empty;
    private EXRRuntimeBuildKind _previousBuildKind;
    private AssetManager? _manager;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "PublishedArchiveDevelopmentLauncher", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _previousBuildKind = XRRuntimeEnvironment.BuildKind;
        AssetDiagnostics.ClearTrackedMissingAssets();
    }

    [TearDown]
    public void TearDown()
    {
        _manager?.Dispose();
        _manager = null;
        AssetManager.ConfigurePublishedArchives(null, null, null);
        AssetManager.ClosePublishedArchives();
        XRRuntimeEnvironment.ConfigureBuildKind(_previousBuildKind);
        AssetDiagnostics.ClearTrackedMissingAssets();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void DevelopmentArchiveBuild_ResolvesTheLodEntriesOfASplitModel_ThroughTheRegistry()
    {
        string archivePath = PackSplitModelArchive(out int lodEntryCount);
        lodEntryCount.ShouldBeGreaterThan(0);

        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.DevelopmentArchives);
        AssetManager.ConfigurePublishedArchives(null, archivePath, null);

        // The build reads the archives, but it is not a published or AOT build, so reflective loading stays available.
        XRRuntimeEnvironment.ReadsPublishedArchives.ShouldBeTrue();
        XRRuntimeEnvironment.IsPublishedBuild.ShouldBeFalse();
        XRRuntimeEnvironment.IsAotRuntimeBuild.ShouldBeFalse();
        XRRuntimeEnvironment.IsDevelopmentBuild.ShouldBeTrue();

        PublishedArchiveRegistry.TryGetRoot(EPublishedContentRoot.GameContent, out PublishedArchiveHandle handle).ShouldBeTrue();
        string requestPath = Path.Combine(_root, "Assets", "Models", "Split.asset");

        using (SubMeshLodPayloads.UseLoadPolicy(ELodPayloadLoadPolicy.All))
        {
            AssetManager.TryLoadPublishedAssetFromArchive(requestPath, typeof(Model), out XRAsset? loaded, out _).ShouldBeTrue();
            Model model = loaded.ShouldBeOfType<Model>();
            SubMesh subMesh = model.Meshes.Single();
            subMesh.HasNonResidentLods.ShouldBeFalse();
            foreach (SubMeshLOD lod in subMesh.LODs)
                lod.Mesh.ShouldNotBeNull();
        }

        using (SubMeshLodPayloads.UseLoadPolicy(ELodPayloadLoadPolicy.ProxyFirst))
        {
            AssetManager.TryLoadPublishedAssetFromArchive(requestPath, typeof(Model), out XRAsset? loaded, out _).ShouldBeTrue();
            SubMesh subMesh = loaded.ShouldBeOfType<Model>().Meshes.Single();
            SubMeshLOD[] deferred = [.. subMesh.LODs.Where(lod => lod.IsPayloadDeferred)];
            deferred.Length.ShouldBe(lodEntryCount);
            foreach (SubMeshLOD lod in deferred)
            {
                handle.Contains(lod.CookedDescriptor.ShouldNotBeNull().EntryName).ShouldBeTrue();
                SubMeshLodPayloads.TryLoadLod(lod.CookedDescriptor!, out XRMesh? mesh, out string? failure).ShouldBeTrue(failure);
                mesh.ShouldNotBeNull();
            }
        }
    }

    [Test]
    public void DevelopmentBuild_WithNoArchiveKind_DoesNotReadTheArchives()
    {
        string archivePath = PackSplitModelArchive(out _);

        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Development);
        AssetManager.ConfigurePublishedArchives(null, archivePath, null);

        XRRuntimeEnvironment.ReadsPublishedArchives.ShouldBeFalse();
        AssetManager.TryLoadPublishedAssetFromArchive(
            Path.Combine(_root, "Assets", "Models", "Split.asset"), typeof(Model), out XRAsset? loaded, out bool searched).ShouldBeFalse();
        searched.ShouldBeFalse();
        loaded.ShouldBeNull();
    }

    [Test]
    public void DevelopmentArchiveBuild_FailsLoudly_ForAnEntryThatDoesNotRead()
    {
        string archivePath = PackSplitModelArchive(out _, cookedRoot =>
            WriteEntry(cookedRoot, "Broken/Bad.asset", [1, 2, 3, 4, 5, 6, 7, 8]));
        ConfigureDevelopmentArchives(archivePath);
        string requestPath = WriteLooseFile("Broken", "Bad.asset");

        InvalidDataException error = Should.Throw<InvalidDataException>(
            () => AssetManager.TryLoadPublishedAssetFromArchive(requestPath, typeof(Model), out _, out _));

        error.Message.ShouldContain("Broken/Bad.asset");
        error.Message.ShouldContain(archivePath);
        error.Message.ShouldContain("loose file is not used");
    }

    [Test]
    public void DevelopmentArchiveBuild_FailsLoudly_ForAStaleContractVersion()
    {
        // The current contract of BuildSettings has a later schema version than the one in this entry.
        byte[] stale = CookedAssetEnvelope.Serialize(new CookedAssetBlob(
            "contract:xre.settings.build:1", CookedAssetFormat.BinaryV2, [1]));
        string archivePath = PackSplitModelArchive(out _, cookedRoot => WriteEntry(cookedRoot, "Stale/Old.asset", stale));
        ConfigureDevelopmentArchives(archivePath);
        string requestPath = WriteLooseFile("Stale", "Old.asset");

        InvalidDataException error = Should.Throw<InvalidDataException>(
            () => AssetManager.TryLoadPublishedAssetFromArchive(requestPath, typeof(BuildSettings), out _, out _));

        error.Message.ShouldContain("Stale/Old.asset");
        error.Message.ShouldContain(archivePath);
        error.InnerException.ShouldNotBeNull().Message.ShouldContain("schema version");
    }

    [Test]
    public void DevelopmentArchiveBuild_MissingEntry_ReturnsFalse_WithOneDiagnostic()
    {
        string archivePath = PackSplitModelArchive(out _);
        ConfigureDevelopmentArchives(archivePath);
        string requestPath = WriteLooseFile("Models", "Missing.asset");

        AssetManager.TryLoadPublishedAssetFromArchive(requestPath, typeof(Model), out XRAsset? loaded, out bool searched)
            .ShouldBeFalse();

        loaded.ShouldBeNull();
        searched.ShouldBeTrue();
        MissingDiagnosticCount("Missing.asset").ShouldBe(1);
    }

    [Test]
    public void DevelopmentArchiveBuild_NullableLoads_ReturnNull_ForAMissingEntry_AndNeverReadTheLooseFile()
    {
        string archivePath = PackSplitModelArchive(out _);
        ConfigureDevelopmentArchives(archivePath);
        // A loose file that would load as a model sits beside the missing entry.
        string loosePath = WriteLooseFile("Models", "Missing.asset");
        AssetManager manager = CreateManager();

        manager.Load<Model>(loosePath, JobPriority.Normal, bypassJobThread: true).ShouldBeNull();
        MissingDiagnosticCount("Missing.asset").ShouldBe(1);

        manager.LoadGameAsset<Model>(JobPriority.Normal, true, "Models", "Other.asset").ShouldBeNull();
        MissingDiagnosticCount("Other.asset").ShouldBe(1);

        // The optional user settings of the launcher: no entry gives null, not an error.
        manager.Load<UserSettings>(Path.Combine(_root, "Config", "user_settings.asset"), JobPriority.Normal, true).ShouldBeNull();
        MissingDiagnosticCount("user_settings.asset").ShouldBe(1);
    }

    [Test]
    public void DevelopmentArchiveBuild_StartupFallback_LoadsTheGameAssetFromTheArchive()
    {
        string archivePath = PackSplitModelArchive(out _);
        ConfigureDevelopmentArchives(archivePath);
        AssetManager manager = CreateManager();

        // The launcher pattern: Load(x) ?? LoadGameAsset(x). The first load misses and gives null, so the fallback runs.
        Model? model = manager.Load<Model>(Path.Combine(_root, "Elsewhere", "Split.asset"), JobPriority.Normal, true)
            ?? manager.LoadGameAsset<Model>(JobPriority.Normal, true, "Models", "Split.asset");

        model.ShouldNotBeNull().Meshes.Count.ShouldBe(1);
    }

    [Test]
    public void DevelopmentArchiveBuild_LoadEngineAsset_Throws_ForAMissingEntry()
    {
        string archivePath = PackSplitModelArchive(out _);
        ConfigureDevelopmentArchives(archivePath);
        AssetManager manager = CreateManager();

        Should.Throw<FileNotFoundException>(
            () => manager.LoadEngineAsset<Model>(JobPriority.Normal, bypassJobThread: true, "Missing", "Engine.asset"));
    }

    [Test]
    public void PublishedBuild_KeepsTheWarning_ForAnEntryThatDoesNotRead()
    {
        string archivePath = PackSplitModelArchive(out _, cookedRoot =>
            WriteEntry(cookedRoot, "Broken/Bad.asset", [1, 2, 3, 4, 5, 6, 7, 8]));
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Published);
        AssetManager.ConfigurePublishedArchives(null, archivePath, null);

        AssetManager.TryLoadPublishedAssetFromArchive(
            Path.Combine(_root, "Assets", "Broken", "Bad.asset"), typeof(Model), out XRAsset? loaded, out bool searched).ShouldBeFalse();
        loaded.ShouldBeNull();
        searched.ShouldBeFalse();
    }

    [TestCase(EXRRuntimeBuildKind.Development, false, false, true)]
    [TestCase(EXRRuntimeBuildKind.DevelopmentArchives, true, false, true)]
    [TestCase(EXRRuntimeBuildKind.Published, true, true, false)]
    [TestCase(EXRRuntimeBuildKind.PublishedAot, true, true, false)]
    public void BuildKind_SetsTheArchiveAndPublishedFlags(
        EXRRuntimeBuildKind kind, bool readsArchives, bool published, bool development)
    {
        XRRuntimeEnvironment.ConfigureBuildKind(kind);

        XRRuntimeEnvironment.ReadsPublishedArchives.ShouldBe(readsArchives);
        XRRuntimeEnvironment.IsPublishedBuild.ShouldBe(published);
        XRRuntimeEnvironment.IsDevelopmentBuild.ShouldBe(development);
        XRRuntimeEnvironment.IsAotRuntimeBuild.ShouldBe(kind == EXRRuntimeBuildKind.PublishedAot);
    }

    /// <summary>Creates the asset manager of a test. The teardown disposes it.</summary>
    private AssetManager CreateManager()
    {
        _manager = new AssetManager
        {
            MonitorGameAssetsForChanges = false,
            GameAssetsPath = Path.Combine(_root, "Assets"),
        };
        return _manager;
    }

    private static int MissingDiagnosticCount(string fileName)
    {
        int count = 0;
        foreach (AssetDiagnostics.MissingAssetInfo info in AssetDiagnostics.GetTrackedMissingAssets())
        {
            if (info.AssetPath.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                count += info.Count;
        }
        return count;
    }

    private static void ConfigureDevelopmentArchives(string archivePath)
    {
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.DevelopmentArchives);
        AssetManager.ConfigurePublishedArchives(null, archivePath, null);
    }

    /// <summary>Writes a loose file at the request path, so a test can show that the loader does not use it.</summary>
    private string WriteLooseFile(string folder, string fileName)
    {
        string path = Path.Combine(_root, "Assets", folder, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "__assetType: XREngine.Rendering.Models.Model");
        return path;
    }

    private static void WriteEntry(string cookedRoot, string entry, byte[] bytes)
    {
        string path = Path.Combine(cookedRoot, entry);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Cooks a model with split LOD payloads, as a non-AOT content cook does, and packs it into one archive.</summary>
    private string PackSplitModelArchive(out int lodEntryCount, Action<string>? addEntries = null)
    {
        string cookedRoot = Path.Combine(_root, "Cooked");
        Directory.CreateDirectory(Path.Combine(cookedRoot, "Models"));
        addEntries?.Invoke(cookedRoot);

        Model model = new(CreateSubMesh());
        using (SubMeshLodPayloadCookSink sink = SubMeshLodPayloadCookSink.Begin(cookedRoot))
        {
            byte[] payload = Serialize(model);
            lodEntryCount = sink.WrittenEntryCount;
            byte[] cooked = CookedAssetEnvelope.Serialize(new CookedAssetBlob(
                CookedAssetTypeReference.Encode(typeof(Model)), CookedAssetFormat.BinaryV2, payload));
            File.WriteAllBytes(Path.Combine(cookedRoot, ModelEntry), cooked);
        }

        string archivePath = Path.Combine(_root, "Content", "GameContent.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        AssetPacker.Pack(cookedRoot, archivePath);
        return archivePath;
    }

#pragma warning disable IL2026, IL3050 // The non-AOT content cook uses the reflective cooked serializer for models.
    private static byte[] Serialize(object value)
        => CookedBinarySerializer.Serialize(value);
#pragma warning restore IL2026, IL3050

    private static SubMesh CreateSubMesh()
    {
        XRMaterial material = new() { Name = "SplitMaterial" };
        return new SubMesh(
            new SubMeshLOD(material, CreateMesh(32, 0.0f), 10.0f),
            new SubMeshLOD(material, CreateMesh(12, 0.1f), 30.0f),
            new SubMeshLOD(material, CreateMesh(4, 0.2f), 1000.0f))
        {
            Name = "SplitSubMesh",
        };
    }

    private static XRMesh CreateMesh(int triangles, float offset)
    {
        Vector3[] positions = new Vector3[triangles * 3];
        for (int i = 0; i < triangles; i++)
        {
            float x = i + offset;
            positions[i * 3] = new Vector3(x, 0.0f, 0.0f);
            positions[(i * 3) + 1] = new Vector3(x + 1.0f, 0.0f, 0.0f);
            positions[(i * 3) + 2] = new Vector3(x, 1.0f, 0.0f);
        }
        XRMesh mesh = XRMesh.CreateTriangles(positions);
        mesh.Name = $"Mesh{triangles}";
        return mesh;
    }
}
