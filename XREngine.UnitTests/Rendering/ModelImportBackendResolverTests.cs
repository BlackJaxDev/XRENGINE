using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ModelImportBackendResolverTests
{
    [Test]
    public void Descriptor_CopiesAndNormalizesSupportedExtensions()
    {
        string[] sourceExtensions = ["FBX", ".obj", ".fbx"];
        ModelImportBackendDescriptor descriptor = new(
            "test.native",
            implementationVersion: 3,
            sourceExtensions,
            priority: 25,
            ModelImportBackendCapabilities.NativeParser | ModelImportBackendCapabilities.StableSourceEntityIds);

        sourceExtensions[0] = ".glb";

        descriptor.SupportedExtensions.ShouldBe(new[] { ".fbx", ".obj" });
        descriptor.SupportsExtension(".FBX").ShouldBeTrue();
        descriptor.SupportsExtension(".glb").ShouldBeFalse();
        descriptor.StableId.ShouldBe("test.native");
        descriptor.ImplementationVersion.ShouldBe(3u);
        descriptor.Priority.ShouldBe(25);
    }

    [Test]
    public void Registry_SnapshotsByPriorityThenStableId_AndRejectsDuplicateIdentity()
    {
        ModelImportBackendDescriptor low = CreateDescriptor("test.low", priority: 10);
        ModelImportBackendDescriptor highB = CreateDescriptor("test.high-b", priority: 20);
        ModelImportBackendDescriptor highA = CreateDescriptor("test.high-a", priority: 20);
        ModelImportBackendRegistry registry = new([low, highB, highA]);

        registry.GetSnapshot()
            .Select(static descriptor => descriptor.StableId)
            .ShouldBe(new[] { "test.high-a", "test.high-b", "test.low" });

        registry.TryRegister(CreateDescriptor("test.new", priority: 5)).ShouldBeTrue();
        registry.TryRegister(CreateDescriptor("test.new", priority: 50)).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => registry.Register(CreateDescriptor("test.low", priority: 100)));
    }

    [Test]
    public void Resolve_AutoGltf_ProducesStableNativeThenAssimpSnapshot()
    {
        ModelImportOptions options = new()
        {
            GltfBackend = GltfImportBackend.Auto,
        };

        ModelImportBackendResolution first = ModelImportBackendResolver.Resolve(
            @"C:\models\example.GLTF",
            options,
            preferredGltfBackend: GltfImportBackend.Auto);
        ModelImportBackendResolution second = ModelImportBackendResolver.Resolve(
            @"C:\other\EXAMPLE.gltf",
            options,
            preferredGltfBackend: GltfImportBackend.Auto);

        first.SourceExtension.ShouldBe(".gltf");
        first.RequestedPolicy.ShouldBe(ModelImportBackendPolicy.Auto);
        first.HostPreference.ShouldBe(ModelImportBackendPolicy.Auto);
        first.ResolverPolicyVersion.ShouldBe(ModelImportBackendResolver.PolicyVersion);
        CandidateIds(first).ShouldBe(new[] { ModelImportBackendIds.NativeGltf, ModelImportBackendIds.Assimp });
        first.CandidateListHash.ShouldBe(second.CandidateListHash);
        first.CandidateListHash.ShouldBe("a19113e0ea56393077215cd448e23eb78d9fcc537c91f4abe1c2f26b331e7a72");
        first.CandidateListHash.Length.ShouldBe(64);
        first.CandidateListHash.ShouldBe(first.CandidateListHash.ToLowerInvariant());
    }

    [Test]
    public void Resolve_ExplicitPolicyAndAutoHostPreference_ProduceExpectedCandidates()
    {
        ModelImportBackendResolution explicitNative = ModelImportBackendResolver.Resolve(
            "asset.glb",
            new ModelImportOptions { GltfBackend = GltfImportBackend.Native },
            preferredGltfBackend: GltfImportBackend.Assimp);
        ModelImportBackendResolution explicitAssimp = ModelImportBackendResolver.Resolve(
            "asset.glb",
            new ModelImportOptions { GltfBackend = GltfImportBackend.Assimp });
        ModelImportBackendResolution autoPrefersAssimp = ModelImportBackendResolver.Resolve(
            "asset.fbx",
            new ModelImportOptions { FbxBackend = FbxImportBackend.Auto },
            preferredFbxBackend: FbxImportBackend.Assimp);
        ModelImportBackendResolution autoPrefersNative = ModelImportBackendResolver.Resolve(
            "asset.fbx",
            new ModelImportOptions { FbxBackend = FbxImportBackend.Auto },
            preferredFbxBackend: FbxImportBackend.Native);
        ModelImportBackendResolution obj = ModelImportBackendResolver.Resolve(
            "asset.obj",
            new ModelImportOptions());

        explicitNative.RequestedPolicy.ShouldBe(ModelImportBackendPolicy.Native);
        CandidateIds(explicitNative).ShouldBe(new[] { ModelImportBackendIds.NativeGltf });
        CandidateIds(explicitAssimp).ShouldBe(new[] { ModelImportBackendIds.Assimp });
        autoPrefersAssimp.RequestedPolicy.ShouldBe(ModelImportBackendPolicy.Auto);
        CandidateIds(autoPrefersAssimp).ShouldBe(new[] { ModelImportBackendIds.Assimp });
        CandidateIds(autoPrefersNative).ShouldBe(new[] { ModelImportBackendIds.NativeFbx, ModelImportBackendIds.Assimp });
        CandidateIds(obj).ShouldBe(new[] { ModelImportBackendIds.Assimp });
    }

    private static ModelImportBackendDescriptor CreateDescriptor(string stableId, int priority)
        => new(
            stableId,
            implementationVersion: 1,
            supportedExtensions: [".test"],
            priority,
            ModelImportBackendCapabilities.NativeParser);

    private static string[] CandidateIds(ModelImportBackendResolution resolution)
        => resolution.Candidates.Select(static descriptor => descriptor.StableId).ToArray();
}
