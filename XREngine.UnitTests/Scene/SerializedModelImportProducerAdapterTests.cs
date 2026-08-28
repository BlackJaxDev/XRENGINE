using NUnit.Framework;
using Shouldly;
using XREngine.Editor.Importers.SerializedAssets;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Prefabs;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class SerializedModelImportProducerAdapterTests
{
    [Test]
    public void CreateReport_AdaptsSourceManifestToSharedProducerContract()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "UnityProducerReport",
            Guid.NewGuid().ToString("N")));
        string sourcePath = Path.Combine(projectRoot, "Assets", "Avatar.prefab");
        SerializedPrefabImportManifest manifest = new()
        {
            EntrySourcePath = "Assets/Avatar.prefab",
            SourceProjectRoot = projectRoot,
            Dependencies =
            [
                CreateDependency(
                    "Assets/Body.fbx",
                    SourceImportDependencyKind.RequiredVisual,
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    localFileId: 1001),
                CreateDependency(
                    "Assets/Albedo.png",
                    SourceImportDependencyKind.RequiredVisual,
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    localFileId: 1002),
                CreateDependency(
                    "Assets/Walk.anim",
                    SourceImportDependencyKind.Animation,
                    "cccccccccccccccccccccccccccccccc",
                    localFileId: 1003),
            ],
        };

        ModelImportProducerReport report = SerializedModelImportProducerAdapter.CreateReport(
            sourcePath,
            new ModelImportOptions(),
            manifest);

        report.BackendSelection.ProducerId
            .ShouldBe(SerializedModelImportProducerAdapter.StableBackendId);
        report.BackendSelection.ProducerVersion
            .ShouldBe(ModelImportBackendVersions.SerializedPrefab);
        report.BackendSelection.Resolution.Candidates[0].StableId
            .ShouldBe(SerializedModelImportProducerAdapter.StableBackendId);
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

    private static SourceImportDependencyManifestEntry CreateDependency(
        string normalizedPath,
        SourceImportDependencyKind kind,
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
