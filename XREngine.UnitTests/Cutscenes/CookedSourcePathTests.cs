using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Cutscenes;
using XREngine.Editor;
using XREngine.Editor.Publishing;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Cutscenes;

/// <summary>
/// A cook writes portable source paths and no file paths into the asset headers of the cooked bytes, and fails when a cooked
/// file would hold a machine path. The residency keys of the cooked textures stay the same as in the editor.
/// </summary>
[TestFixture, NonParallelizable]
public sealed class CookedSourcePathTests
{
    private string _tempRoot = null!;
    private string _assets = null!;
    private string _imagePath = null!;

    /// <summary>A cooked asset that holds a texture and a material.</summary>
    public sealed class CookHolderAsset : XRAsset
    {
        public XRTexture2D? Texture { get; set; }
        public XRMaterial? Material { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>An asset whose object array is too large for the graph refresh, so the refresh throws.</summary>
    public sealed class OversizedGraphAsset : XRAsset
    {
        public XRTexture2D?[] Items { get; set; } = [];
    }

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "CookedSourcePath", Guid.NewGuid().ToString("N"));
        _assets = Path.Combine(_tempRoot, "Assets");
        _imagePath = Path.Combine(_assets, "Textures", "floor.png");
        Directory.CreateDirectory(Path.GetDirectoryName(_imagePath)!);
        File.WriteAllBytes(_imagePath, [0x89, 0x50, 0x4E, 0x47]);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public void CookScope_WritesAPortableSourcePathAndNoFilePath_AndDoesNotChangeTheAsset()
    {
        XRTexture2D texture = new() { Name = "floor", FilePath = _imagePath, OriginalPath = _imagePath, SamplerName = "_MainTex" };
        CookHolderAsset holder = new() { Name = "holder", OriginalPath = Path.Combine(_assets, "Data", "holder.bin"), Texture = texture };
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
            byte[] cooked;
            using (CookedSourcePathScope scope = CookedSourcePathProjection.Begin())
            {
                cooked = CookedBinarySerializer.Serialize(holder);
                scope.Failures.ShouldBeEmpty();
            }

            CookHolderAsset loaded = CookedBinarySerializer.Deserialize(typeof(CookHolderAsset), cooked).ShouldBeOfType<CookHolderAsset>();
            loaded.OriginalPath.ShouldBe("game://Data/holder.bin");
            XRTexture2D loadedTexture = loaded.Texture.ShouldNotBeNull();
            loadedTexture.OriginalPath.ShouldBe("game://Textures/floor.png");
            loadedTexture.FilePath.ShouldBeNull();
            ContainsText(cooked, _tempRoot).ShouldBeFalse();

            // The projection reads the assets and does not change them.
            texture.OriginalPath.ShouldBe(_imagePath);
            texture.FilePath.ShouldBe(_imagePath);
            holder.OriginalPath.ShouldBe(Path.Combine(_assets, "Data", "holder.bin"));

            // Outside a cook scope the paths are written as they are.
            ContainsText(CookedBinarySerializer.Serialize(holder), _tempRoot).ShouldBeTrue();
        }
        finally
        {
            holder.Destroy(now: true);
            texture.Destroy(now: true);
        }
    }

    [Test]
    public void CookScope_RecordsAnAbsolutePathOutsideTheAssetRoots_AndKeepsRelativeValues()
    {
        string outside = Path.Combine(_tempRoot, "Elsewhere", "wall.png");
        XRTexture2D texture = new() { Name = "wall", OriginalPath = outside };
        CookHolderAsset holder = new() { Name = "holder", OriginalPath = "generated", Texture = texture };
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
            using CookedSourcePathScope scope = CookedSourcePathProjection.Begin();

            _ = CookedBinarySerializer.Serialize(holder);

            scope.Failures.ShouldBe([new CookedSourcePathFailure("wall", outside)]);
        }
        finally
        {
            holder.Destroy(now: true);
            texture.Destroy(now: true);
        }
    }

    [Test]
    public void ContentCook_OfAMaterialWithASourceImageTexture_WritesAPortablePath_ThatKeepsTheKeyAndResolves()
    {
        // An imported material texture: a source image backs it. The second texture has only the source path.
        XRTexture2D backed = new() { Name = "floor", FilePath = _imagePath, OriginalPath = _imagePath, SamplerName = "_MainTex" };
        XRTexture2D sourceOnly = new() { Name = "floor detail", OriginalPath = _imagePath, SamplerName = "_DetailTex" };
        XRMaterial material = new([backed, sourceOnly]) { Name = "FloorMaterial" };
        XRMaterial? cooked = null;
        IRuntimeRenderingHostServices? previousRendering = RuntimeRenderingHostServices.Current;
        try
        {
            Guid backedKey;
            Guid sourceOnlyKey;
            string cookedDirectory;
            using (AssetSerializationServices.Install(new FakeAssetRootServices(_assets)))
            {
                backedKey = CutsceneResidencyAssetKeys.ForTexture(backed);
                sourceOnlyKey = CutsceneResidencyAssetKeys.ForTexture(sourceOnly);
                WriteAsset(Path.Combine(_assets, "FloorMaterial.asset"), material);

                cookedDirectory = Cook();
                cooked = CookedAssetReader.LoadAsset<XRMaterial>(File.ReadAllBytes(Path.Combine(cookedDirectory, "FloorMaterial.asset"))).ShouldNotBeNull();
            }

            AssertNoCookRoot(cookedDirectory);
            XRTexture2D cookedBacked = cooked.Textures[0].ShouldBeOfType<XRTexture2D>();
            XRTexture2D cookedSourceOnly = cooked.Textures[1].ShouldBeOfType<XRTexture2D>();
            cookedBacked.OriginalPath.ShouldBe("game://Textures/floor.png");
            cookedSourceOnly.OriginalPath.ShouldBe("game://Textures/floor.png");
            cookedBacked.FilePath.ShouldBeNull();

            // Another machine: other asset roots. The baked keys still match.
            using (AssetSerializationServices.Install(new FakeAssetRootServices(Path.Combine(_tempRoot, "OtherMachine", "Assets"))))
            {
                CutsceneResidencyAssetKeys.ForTexture(cookedBacked, out bool backedMachineDependent).ShouldBe(backedKey);
                CutsceneResidencyAssetKeys.ForTexture(cookedSourceOnly, out bool sourceOnlyMachineDependent).ShouldBe(sourceOnlyKey);
                backedMachineDependent.ShouldBeFalse();
                sourceOnlyMachineDependent.ShouldBeFalse();
            }

            // The cook machine: the portable path resolves to the source image. The restore sets the empty file path to the
            // resolved file, so the streaming record points at a file that exists.
            RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
            using RendererBackendCatalog catalog = new();
            using IDisposable registrations = BuiltInRendererBackendModules.RegisterAll(catalog);
            using IDisposable cookMachine = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
            AssetReferencePath.TryResolveFileSystemPath(cookedBacked.OriginalPath!, out string? resolved).ShouldBeTrue();
            resolved.ShouldBe(Path.GetFullPath(_imagePath));
            File.Exists(resolved).ShouldBeTrue();
            cookedBacked.TryRestoreImportedTextureStreamingSource().ShouldBeTrue();
            cookedBacked.FilePath.ShouldBe(resolved);
            XRTexture2D.GetImportedTextureStreamingTextureTelemetry()
                .Where(entry => string.Equals(entry.FilePath, resolved, StringComparison.OrdinalIgnoreCase))
                .ShouldNotBeEmpty();
        }
        finally
        {
            RuntimeRenderingHostServices.Current = previousRendering!;
            DestroyCooked(cooked);
            material.Destroy(now: true);
            backed.Destroy(now: true);
            sourceOnly.Destroy(now: true);
        }
    }

    [Test]
    public void ContentCook_OfAMaterialWithALiveTextureInItsList_WritesPortablePaths_AndDoesNotChangeTheLiveTexture()
    {
        string textureAssetPath = Path.Combine(_assets, "Textures", "floor.asset");
        XRTexture2D live = new() { Name = "live floor", OriginalPath = _imagePath, SamplerName = "_MainTex" };
        XRMaterial material = new([live]) { Name = "LiveTextureMaterial" };
        XRMaterial? cooked = null;
        try
        {
            FakeAssetRootServices services = new(_assets);
            using (AssetSerializationServices.Install(services))
            {
                WriteAsset(textureAssetPath, live);
                live.FilePath = textureAssetPath;
                services.LiveAssets.Add(live.ID, live);
                WriteAsset(Path.Combine(_assets, "LiveTextureMaterial.asset"), material);
                File.ReadAllText(Path.Combine(_assets, "LiveTextureMaterial.asset")).ShouldContain(live.ID.ToString());

                string cookedDirectory = Cook();
                AssertNoCookRoot(cookedDirectory);
                cooked = CookedAssetReader.LoadAsset<XRMaterial>(File.ReadAllBytes(Path.Combine(cookedDirectory, "LiveTextureMaterial.asset"))).ShouldNotBeNull();
            }

            XRTexture2D cookedTexture = cooked.Textures[0].ShouldBeOfType<XRTexture2D>();
            cookedTexture.OriginalPath.ShouldBe("game://Textures/floor.png");
            cookedTexture.FilePath.ShouldBeNull();
            live.OriginalPath.ShouldBe(_imagePath);
            live.FilePath.ShouldBe(textureAssetPath);
            live.SourceAsset.ShouldBeSameAs(live);
        }
        finally
        {
            DestroyCooked(cooked);
            material.Destroy(now: true);
            live.Destroy(now: true);
        }
    }

    [Test]
    public void ContentCook_OfAnAssetThatReferencesALiveMaterialAsset_WritesPortablePaths_AndDoesNotChangeTheLiveMaterial()
    {
        // A prefab-like asset references a material that is its own asset file. The cook load gets the live material, and the
        // cook writes it inline in the cooked bytes.
        string materialAssetPath = Path.Combine(_assets, "Materials", "Floor.asset");
        Directory.CreateDirectory(Path.GetDirectoryName(materialAssetPath)!);
        XRTexture2D backed = new() { Name = "floor", FilePath = _imagePath, OriginalPath = _imagePath, SamplerName = "_MainTex" };
        XRMaterial live = new([backed]) { Name = "LiveFloor" };
        CookHolderAsset holder = new() { Name = "Prefab", Material = live };
        CookHolderAsset? cooked = null;
        try
        {
            FakeAssetRootServices services = new(_assets);
            using (AssetSerializationServices.Install(services))
            {
                WriteAsset(materialAssetPath, live);
                live.FilePath = materialAssetPath;
                services.LiveAssets.Add(live.ID, live);
                WriteAsset(Path.Combine(_assets, "Prefab.asset"), holder);
                File.ReadAllText(Path.Combine(_assets, "Prefab.asset")).ShouldContain("game://Materials/Floor.asset");

                string cookedDirectory = Cook();
                AssertNoCookRoot(cookedDirectory);
                cooked = CookedAssetReader.LoadAsset<CookHolderAsset>(File.ReadAllBytes(Path.Combine(cookedDirectory, "Prefab.asset"))).ShouldNotBeNull();
            }

            XRMaterial cookedMaterial = cooked.Material.ShouldNotBeNull();
            cookedMaterial.ShouldNotBeSameAs(live);
            cookedMaterial.FilePath.ShouldBeNull();
            XRTexture2D cookedTexture = cookedMaterial.Textures[0].ShouldBeOfType<XRTexture2D>();
            cookedTexture.OriginalPath.ShouldBe("game://Textures/floor.png");
            cookedTexture.FilePath.ShouldBeNull();
            live.FilePath.ShouldBe(materialAssetPath);
            backed.OriginalPath.ShouldBe(_imagePath);
            backed.FilePath.ShouldBe(_imagePath);
        }
        finally
        {
            if (cooked?.Material is { } cookedMaterial)
                DestroyCooked(cookedMaterial);
            cooked?.Destroy(now: true);
            holder.Destroy(now: true);
            live.Destroy(now: true);
            backed.Destroy(now: true);
        }
    }

    [Test]
    public void ContentCook_FailsForASourcePathOutsideTheAssetRoots_AndNamesTheAssetAndThePath()
    {
        string outside = Path.Combine(_tempRoot, "Downloads", "wall.png");
        XRTexture2D texture = new() { Name = "downloaded wall", OriginalPath = outside, SamplerName = "_MainTex" };
        XRMaterial material = new([texture]) { Name = "WallMaterial" };
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
            WriteAsset(Path.Combine(_assets, "WallMaterial.asset"), material);

            CookedSourcePathException error = Should.Throw<CookedSourcePathException>(() => Cook());

            error.Message.ShouldContain("WallMaterial.asset");
            error.Message.ShouldContain("downloaded wall");
            error.Message.ShouldContain(outside);
        }
        finally
        {
            material.Destroy(now: true);
            texture.Destroy(now: true);
        }
    }

    [Test]
    public void ContentCook_FailsWhenTheCookedBytesHoldAMachinePath_AndNamesTheAssetAndThePath()
    {
        // A member that is not a source path holds an absolute path. The write-time projection does not see it, so the check
        // after the cook finds it.
        string machinePath = Path.Combine(_tempRoot, "Notes", "readme.txt");
        CookHolderAsset holder = new() { Name = "Noted", Note = machinePath };
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
            WriteAsset(Path.Combine(_assets, "Noted.asset"), holder);

            CookedSourcePathException error = Should.Throw<CookedSourcePathException>(() => Cook());

            error.Message.ShouldContain("Noted.asset");
            error.Message.ShouldContain(_tempRoot);
        }
        finally
        {
            holder.Destroy(now: true);
        }
    }

    [Test]
    public void ContentCook_FailsForAnAssetThatDoesNotCook_AndForAYamlWithNoTypeHint_AndShipsNoYaml()
    {
        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        string broken = Path.Combine(_assets, "Broken.asset");
        File.WriteAllText(broken, $"__assetType: XREngine.UnitTests.NoSuchAssetType{Environment.NewLine}OriginalPath: {_imagePath}{Environment.NewLine}");
        string intermediate = Path.Combine(_tempRoot, "Intermediate", "broken");

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            ProjectBuilder.PrepareCookedContentDirectoryForTests(_assets, intermediate));
        error.Message.ShouldContain("Broken.asset");
        error.Message.ShouldContain("does not ship YAML sources");
        Directory.GetFiles(intermediate, "Broken.asset", SearchOption.AllDirectories).ShouldBeEmpty();

        File.WriteAllText(broken, $"Name: untyped{Environment.NewLine}OriginalPath: {_imagePath}{Environment.NewLine}");
        error = Should.Throw<InvalidOperationException>(() => Cook());
        error.Message.ShouldContain("Broken.asset");
        error.Message.ShouldContain("no __assetType");
    }

    [Test]
    public void ContentCook_FailsWhenTheLiveAssetRefreshFails()
    {
        OversizedGraphAsset asset = new() { Name = "oversized", Items = new XRTexture2D?[1001] };
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
            Should.Throw<InvalidDataException>(() => XRAssetGraphUtility.RefreshAssetGraph(asset));
            WriteAsset(Path.Combine(_assets, "Oversized.asset"), asset);
            string intermediate = Path.Combine(_tempRoot, "Intermediate", "oversized");

            Should.Throw<InvalidDataException>(() => ProjectBuilder.PrepareCookedContentDirectoryForTests(_assets, intermediate));
            Directory.GetFiles(intermediate, "Oversized.asset", SearchOption.AllDirectories).ShouldBeEmpty();
        }
        finally
        {
            asset.Destroy(now: true);
        }
    }

    [Test]
    public void IncrementalContentCook_FailsWhenTheCookedBytesHoldAMachinePath()
    {
        // The startup settings have a registered runtime codec, so the published cook accepts them. A free folder string
        // holds an absolute path that the source path projection does not see.
        string machinePath = Path.Combine(_tempRoot, "Textures", "Runtime");
        GameStartupSettings settings = new() { Name = "Startup", TexturesFolder = machinePath };
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
            string source = Path.Combine(_assets, "Data", "Startup.asset");
            WriteAsset(source, settings);
            string destination = Path.Combine(_tempRoot, "Delta", "Data", "Startup.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            CookedSourcePathException error = Should.Throw<CookedSourcePathException>(() =>
                ProjectBuilder.TransformIncrementalContentFile(source, destination));
            error.Message.ShouldContain("Startup.asset");
            error.Message.ShouldContain(_tempRoot);
        }
        finally
        {
            settings.Destroy(now: true);
        }
    }

    [Test]
    public void ConfigArchive_WritesSettingsWithNoMachinePath_AndDoesNotChangeTheLiveSettings()
    {
        string configDirectory = Path.Combine(_tempRoot, "Config");
        GameStartupSettings startup = new() { Name = "Startup", FilePath = Path.Combine(configDirectory, "startup.asset") };
        EditorPreferences preferences = new() { Name = "Preferences", FilePath = Path.Combine(configDirectory, "editor_preferences.asset") };
        UserSettings user = new() { Name = "User", FilePath = Path.Combine(configDirectory, "user_settings.asset") };
        string staging = Path.Combine(_tempRoot, "Staging");
        Directory.CreateDirectory(staging);
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));

            ProjectBuilder.WriteConfigArchiveAssets(staging, startup, preferences, user, aotMetadata: null);

            string[] files = Directory.GetFiles(staging);
            files.Length.ShouldBe(3);
            foreach (string file in files)
                ContainsText(File.ReadAllBytes(file), _tempRoot).ShouldBeFalse(file);
            UserSettings cookedUser = CookedAssetReader.LoadAsset<UserSettings>(File.ReadAllBytes(Path.Combine(staging, "user_settings.asset"))).ShouldNotBeNull();
            cookedUser.FilePath.ShouldBeNull();
            cookedUser.Name.ShouldBe("User");
            user.FilePath.ShouldBe(Path.Combine(configDirectory, "user_settings.asset"));
            startup.FilePath.ShouldBe(Path.Combine(configDirectory, "startup.asset"));
        }
        finally
        {
            startup.Destroy(now: true);
            preferences.Destroy(now: true);
            user.Destroy(now: true);
        }
    }

    [Test]
    public void ConfigArchive_FailsForAnAbsoluteTexturesFolder_AndForASourcePathOutsideTheAssetRoots()
    {
        string staging = Path.Combine(_tempRoot, "Staging");
        Directory.CreateDirectory(staging);
        string machinePath = Path.Combine(_tempRoot, "Textures");
        GameStartupSettings startup = new() { Name = "Startup", TexturesFolder = machinePath };
        EditorPreferences preferences = new() { Name = "Preferences" };
        UserSettings user = new() { Name = "User" };
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));

            CookedSourcePathException error = Should.Throw<CookedSourcePathException>(() =>
                ProjectBuilder.WriteConfigArchiveAssets(staging, startup, preferences, user, aotMetadata: null));
            error.Message.ShouldContain(nameof(GameStartupSettings.TexturesFolder));
            error.Message.ShouldContain(machinePath);

            startup.TexturesFolder = "Textures";
            string outside = Path.Combine(_tempRoot, "Elsewhere", "user.yaml");
            user.OriginalPath = outside;
            error = Should.Throw<CookedSourcePathException>(() =>
                ProjectBuilder.WriteConfigArchiveAssets(staging, startup, preferences, user, aotMetadata: null));
            error.Message.ShouldContain(outside);
        }
        finally
        {
            startup.Destroy(now: true);
            preferences.Destroy(now: true);
            user.Destroy(now: true);
        }
    }

    [Test]
    public void OutputGuard_FindsUtf8AndUtf16Paths_AndIgnoresPortableReferencesAndRandomBytes()
    {
        string path = Path.Combine("C:\\", "Users", "someone", "Assets", "floor.png");
        CookedOutputPathGuard.TryFindMachinePath([0, 3, .. Encoding.UTF8.GetBytes(path), 0], [], out string? utf8).ShouldBeTrue();
        utf8.ShouldBe(path);
        CookedOutputPathGuard.TryFindMachinePath([7, .. Encoding.Unicode.GetBytes(path), 0], [], out string? utf16).ShouldBeTrue();
        utf16.ShouldBe(path);
        CookedOutputPathGuard.TryFindMachinePath(Encoding.UTF8.GetBytes(@"\\server\share\folder\floor.png"), [], out _).ShouldBeTrue();
        CookedOutputPathGuard.TryFindMachinePath(Encoding.UTF8.GetBytes("game://Textures/floor.png engine://Shaders/a/b.fs http://host/a/b"), [], out _).ShouldBeFalse();

        byte[] random = new byte[4 * 1024 * 1024];
        new Random(1234).NextBytes(random);
        CookedOutputPathGuard.TryFindMachinePath(random, [], out _).ShouldBeFalse();
    }

    [Test]
    public void OutputGuard_FindsPathsWithNonAsciiParts_InUtf8AndUtf16()
    {
        string[] paths =
        [
            Path.Combine("C:\\", "Users", "\u0418\u0432\u0430\u043D", "Downloads", "tex.png"),
            Path.Combine("D:\\", "\u30E6\u30FC\u30B6\u30FC", "\u7D20\u6750", "floor.png"),
            Path.Combine("E:\\", "Users", "Jos\u00E9 \U0001F600", "Assets", "wall.png"),
            "\\\\server\\\u5171\u6709\\\u30C6\u30AF\u30B9\u30C1\u30E3\\floor.png",
        ];
        foreach (string path in paths)
        {
            CookedOutputPathGuard.TryFindMachinePath([0, 9, .. Encoding.UTF8.GetBytes(path), 0], [], out string? utf8).ShouldBeTrue(path);
            utf8.ShouldBe(path);
            CookedOutputPathGuard.TryFindMachinePath([5, .. Encoding.Unicode.GetBytes(path), 0, 0], [], out string? utf16).ShouldBeTrue(path);
            utf16.ShouldBe(path);
        }
    }

    private string Cook()
        => ProjectBuilder.PrepareCookedContentDirectoryForTests(_assets, Path.Combine(_tempRoot, "Intermediate", Guid.NewGuid().ToString("N")));

    private void AssertNoCookRoot(string cookedDirectory)
    {
        foreach (string file in Directory.GetFiles(cookedDirectory, "*.asset", SearchOption.AllDirectories))
            ContainsText(File.ReadAllBytes(file), _tempRoot).ShouldBeFalse(file);
    }

    private static bool ContainsText(byte[] bytes, string text)
        => IndexOfIgnoreCase(Encoding.Latin1.GetString(bytes), Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text)))
        || IndexOfIgnoreCase(Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1), text)
        || bytes.Length > 1 && IndexOfIgnoreCase(Encoding.Unicode.GetString(bytes, 1, (bytes.Length - 1) & ~1), text);

    private static bool IndexOfIgnoreCase(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static void WriteAsset(string path, XRAsset asset)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string yaml = AssetManager.Serializer.Serialize(asset);
        if (!yaml.Contains("__assetType:", StringComparison.Ordinal))
            yaml = $"__assetType: {asset.GetType().FullName}{Environment.NewLine}{yaml}";
        File.WriteAllText(path, yaml);
    }

    private static void DestroyCooked(XRMaterial? material)
    {
        if (material is null)
            return;

        foreach (XRTexture? texture in material.Textures)
            texture?.Destroy(now: true);
        material.Destroy(now: true);
    }
}
