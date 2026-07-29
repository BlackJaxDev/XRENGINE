using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Models.Caching;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AssimpImportReportBuilderTests
{
    [Test]
    public void Build_ReportsObjMaterialLibraryAsStructuralDependency()
    {
        string tempDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "AssimpProducerReport",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string materialPath = Path.Combine(tempDirectory, "materials.mtl");
        string sourcePath = Path.Combine(tempDirectory, "model.obj");
        File.WriteAllText(materialPath, "newmtl Default");
        File.WriteAllText(sourcePath, """
mtllib materials.mtl
v 0 0 0
v 1 0 0
v 0 1 0
f 1 2 3
""");

        try
        {
            Assimp.Scene scene = new();
            ModelImportProducerMetadata metadata = AssimpImportReportBuilder.Build(
                sourcePath,
                scene);

            metadata.Dependencies.ShouldContain(static dependency =>
                dependency.Kind == ModelImportDependencyKind.EntrySource
                && dependency.IsRequired);
            metadata.Dependencies.ShouldContain(dependency =>
                dependency.Kind == ModelImportDependencyKind.Structural
                && dependency.IsRequired
                && dependency.NormalizedPath == ModelImportPathNormalizer.NormalizeAbsolutePath(materialPath)
                && dependency.ProducerKey == "obj:mtllib:0");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
