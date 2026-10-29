using NUnit.Framework;
using Shouldly;
using System.Security.Cryptography;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class CookedBakeInputLoaderTests
{
    private const string AvatarAssetPath = "Fixtures/Avatar.asset";
    private const string WorldAssetPath = "Fixtures/World.asset";
    private static readonly byte[] PlaceholderAssetBytes = [0x01, 0x02, 0x03, 0x04];

    private string _tempRoot = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "CookedBakeInputLoader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public void Load_PakWithExecutableEntry_ThrowsContentRejected()
    {
        string pak = Path.Combine(_tempRoot, "executable.pak");
        GaussianBakeTestAssets.WritePak(pak,
            (AvatarAssetPath, PlaceholderAssetBytes),
            ("Scripts/evil.dll", [(byte)'M', (byte)'Z', 0x90, 0x00]));

        Should.Throw<GaussianBakeException>(() => CookedBakeInputLoader.Load(pak, AvatarAssetPath, GaussianBakeKind.Avatar))
            .FailureCode.ShouldBe("content-rejected");
    }

    [Test]
    public void Load_PakWithExecutableSignatureOnly_ThrowsContentRejected()
    {
        string pak = Path.Combine(_tempRoot, "signature.pak");
        GaussianBakeTestAssets.WritePak(pak,
            (AvatarAssetPath, PlaceholderAssetBytes),
            ("Textures/payload.bin", [0x7F, (byte)'E', (byte)'L', (byte)'F', 0x02]));

        Should.Throw<GaussianBakeException>(() => CookedBakeInputLoader.Load(pak, AvatarAssetPath, GaussianBakeKind.Avatar))
            .FailureCode.ShouldBe("content-rejected");
    }

    [Test]
    public void Load_PakWithEscapingPath_ThrowsContentRejected()
    {
        string pak = Path.Combine(_tempRoot, "escaping.pak");
        GaussianBakeTestAssets.WritePakWithEscapingEntry(
            pak,
            [(AvatarAssetPath, PlaceholderAssetBytes)],
            "outside.asset",
            PlaceholderAssetBytes);
        AssetPacker.GetAssetPaths(pak).ShouldContain("../outside.asset");

        Should.Throw<GaussianBakeException>(() => CookedBakeInputLoader.Load(pak, AvatarAssetPath, GaussianBakeKind.Avatar))
            .FailureCode.ShouldBe("content-rejected");
    }

    [Test]
    public void Load_MissingAsset_ThrowsInputLoadFailed()
    {
        string pak = Path.Combine(_tempRoot, "missing.pak");
        GaussianBakeTestAssets.WritePak(pak, ("Fixtures/Other.asset", PlaceholderAssetBytes));

        Should.Throw<GaussianBakeException>(() => CookedBakeInputLoader.Load(pak, AvatarAssetPath, GaussianBakeKind.Avatar))
            .FailureCode.ShouldBe("input-load-failed");
    }

    [Test]
    public void Load_MissingPakFile_ThrowsInputLoadFailed()
    {
        string pak = Path.Combine(_tempRoot, "absent.pak");

        Should.Throw<GaussianBakeException>(() => CookedBakeInputLoader.Load(pak, AvatarAssetPath, GaussianBakeKind.Avatar))
            .FailureCode.ShouldBe("input-load-failed");
    }

    [Test]
    public void Load_PakWithPrefab_ReturnsRootWithReadableMeshPositions()
    {
        XRPrefabSource prefab = GaussianBakeTestAssets.ImportPrefab(GaussianBakeTestAssets.SkinnedAvatarModelPath);
        string pak = Path.Combine(_tempRoot, "avatar.pak");
        GaussianBakeTestAssets.WritePak(pak, (AvatarAssetPath, GaussianBakeTestAssets.Cook(prefab)));

        CookedBakeInput input = CookedBakeInputLoader.Load(pak, AvatarAssetPath, GaussianBakeKind.Avatar);

        input.Kind.ShouldBe(GaussianBakeKind.Avatar);
        input.AssetPath.ShouldBe(AvatarAssetPath);
        input.World.ShouldBeNull();
        input.PrefabRoot.ShouldNotBeNull();
        SubMesh subMesh = GaussianBakeTestAssets.FirstSubMesh(input.PrefabRoot).ShouldNotBeNull();
        XRMesh mesh = subMesh.LODs.Min.ShouldNotBeNull().Mesh.ShouldNotBeNull();
        mesh.VertexCount.ShouldBeGreaterThan(0);
        float.IsFinite(mesh.GetPosition(0).X).ShouldBeTrue();
        GaussianBakeTestAssets.CountClipComponents(input.PrefabRoot, requireAnimation: true).ShouldBeGreaterThan(0);
        input.InputSha256.Length.ShouldBe(64);
        input.InputSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pak))));
    }

    [Test]
    public void Load_PakWithWorld_ReturnsWorldWithReadableMeshPositions()
    {
        string pak = Path.Combine(_tempRoot, "world.pak");
        GaussianBakeTestAssets.WritePak(pak, (WorldAssetPath, GaussianBakeTestAssets.Cook(CreateAvatarWorld())));

        CookedBakeInput input = CookedBakeInputLoader.Load(pak, WorldAssetPath, GaussianBakeKind.World);

        input.Kind.ShouldBe(GaussianBakeKind.World);
        input.PrefabRoot.ShouldBeNull();
        XRWorld world = input.World.ShouldNotBeNull();
        world.Scenes.Count.ShouldBe(1);
        SceneNode root = world.Scenes[0].RootNodes.ShouldHaveSingleItem();
        SubMesh subMesh = GaussianBakeTestAssets.FirstSubMesh(root).ShouldNotBeNull();
        XRMesh mesh = subMesh.LODs.Min.ShouldNotBeNull().Mesh.ShouldNotBeNull();
        mesh.VertexCount.ShouldBeGreaterThan(0);
        float.IsFinite(mesh.GetPosition(0).X).ShouldBeTrue();
    }

    [Test]
    public void Load_WorldAssetAsAvatar_ThrowsInputLoadFailed()
    {
        string pak = Path.Combine(_tempRoot, "world-as-avatar.pak");
        GaussianBakeTestAssets.WritePak(pak, (WorldAssetPath, GaussianBakeTestAssets.Cook(CreateAvatarWorld())));

        Should.Throw<GaussianBakeException>(() => CookedBakeInputLoader.Load(pak, WorldAssetPath, GaussianBakeKind.Avatar))
            .FailureCode.ShouldBe("input-load-failed");
    }

    private static XRWorld CreateAvatarWorld()
    {
        XRPrefabSource prefab = GaussianBakeTestAssets.ImportPrefab(GaussianBakeTestAssets.SkinnedAvatarModelPath);
        return new XRWorld("BakeTestWorld", new XRScene("BakeTestScene", prefab.Instantiate()));
    }
}
