using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
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

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "PublishedArchiveDevelopmentLauncher", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _previousBuildKind = XRRuntimeEnvironment.BuildKind;
    }

    [TearDown]
    public void TearDown()
    {
        AssetManager.ConfigurePublishedArchives(null, null, null);
        AssetManager.ClosePublishedArchives();
        XRRuntimeEnvironment.ConfigureBuildKind(_previousBuildKind);
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
            AssetManager.TryLoadPublishedAssetFromArchive(requestPath, typeof(Model), out XRAsset? loaded).ShouldBeTrue();
            Model model = loaded.ShouldBeOfType<Model>();
            SubMesh subMesh = model.Meshes.Single();
            subMesh.HasNonResidentLods.ShouldBeFalse();
            foreach (SubMeshLOD lod in subMesh.LODs)
                lod.Mesh.ShouldNotBeNull();
        }

        using (SubMeshLodPayloads.UseLoadPolicy(ELodPayloadLoadPolicy.ProxyFirst))
        {
            AssetManager.TryLoadPublishedAssetFromArchive(requestPath, typeof(Model), out XRAsset? loaded).ShouldBeTrue();
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
            Path.Combine(_root, "Assets", "Models", "Split.asset"), typeof(Model), out XRAsset? loaded).ShouldBeFalse();
        loaded.ShouldBeNull();
    }

    /// <summary>Cooks a model with split LOD payloads, as a non-AOT content cook does, and packs it into one archive.</summary>
    private string PackSplitModelArchive(out int lodEntryCount)
    {
        string cookedRoot = Path.Combine(_root, "Cooked");
        Directory.CreateDirectory(Path.Combine(cookedRoot, "Models"));

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
