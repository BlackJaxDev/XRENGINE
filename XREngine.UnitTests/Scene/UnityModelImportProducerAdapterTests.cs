using NUnit.Framework;
using Shouldly;
using XREngine.ModelCaching;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Prefabs;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class UnityModelImportProducerAdapterTests
{
    [Test]
    public void CreateReport_AdaptsUnityManifestToSharedProducerContract()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "UnityProducerReport",
            Guid.NewGuid().ToString("N")));
        string sourcePath = Path.Combine(projectRoot, "Assets", "Avatar.prefab");
        UnityPrefabImportManifest manifest = new()
        {
            EntrySourcePath = "Assets/Avatar.prefab",
            UnityProjectRoot = projectRoot,
            Dependencies =
            [
                CreateDependency(
                    "Assets/Body.fbx",
                    UnityImportDependencyKind.RequiredVisual,
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    localFileId: 1001),
                CreateDependency(
                    "Assets/Albedo.png",
                    UnityImportDependencyKind.RequiredVisual,
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    localFileId: 1002),
                CreateDependency(
                    "Assets/Walk.anim",
                    UnityImportDependencyKind.Animation,
                    "cccccccccccccccccccccccccccccccc",
                    localFileId: 1003),
            ],
        };

        ModelImportProducerReport report = UnityModelImportProducerAdapter.CreateReport(
            sourcePath,
            new ModelImportOptions(),
            manifest);

        report.BackendSelection.ProducerId
            .ShouldBe(UnityModelImportProducerAdapter.StableBackendId);
        report.BackendSelection.ProducerVersion
            .ShouldBe(ModelImportBackendVersions.UnityPrefab);
        report.BackendSelection.Resolution.Candidates[0].StableId
            .ShouldBe(UnityModelImportProducerAdapter.StableBackendId);
        report.Dependencies.ShouldContain(static dependency =>
            dependency.Kind == ModelImportDependencyKind.EntrySource
            && dependency.IsRequired);
        report.Dependencies.ShouldContain(static dependency =>
            dependency.Kind == ModelImportDependencyKind.Structural
            && dependency.ProducerKey == "unity:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:1001");
        report.Dependencies.ShouldContain(static dependency =>
            dependency.Kind == ModelImportDependencyKind.ReferencedTexture
            && dependency.IsRequired);
        report.Dependencies.ShouldContain(static dependency =>
            dependency.Kind == ModelImportDependencyKind.ReferencedAnimation);
        report.SourceEntities.ShouldContain(static entity =>
            entity.Key == "unity:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb:1002"
            && entity.Kind == ModelImportEntityKind.Texture
            && entity.IsStable);
        report.ReferenceKeys.ShouldContain(static reference =>
            reference.Kind == ModelImportReferenceKind.Texture);
        report.ReferenceKeys.ShouldContain(static reference =>
            reference.Kind == ModelImportReferenceKind.Animation);
    }

    private static UnityImportDependencyManifestEntry CreateDependency(
        string normalizedPath,
        UnityImportDependencyKind kind,
        string guid,
        long localFileId)
        => new()
        {
            NormalizedPath = normalizedPath,
            Kind = kind,
            SourceGuid = guid,
            LocalFileId = localFileId,
            Length = 42,
            LastWriteTimeUtcTicks = 123456,
            Sha256 = new string('a', 64),
        };
}
