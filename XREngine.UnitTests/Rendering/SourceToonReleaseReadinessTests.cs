using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SourceToonReleaseReadinessTests
{
    [Test]
    public void PublicSupportStatementMatchesExecutableCatalogAndDiagnostics()
    {
        using JsonDocument catalog = OpenCatalog();
        JsonElement source = catalog.RootElement.GetProperty("source");
        source.GetProperty("shaderVersion").GetString().ShouldBe(SourceToon93Catalog.VersionText);
        source.GetProperty("commit").GetString().ShouldBe(SourceToon93Catalog.RepositoryCommit);
        catalog.RootElement.GetProperty("summary")
            .GetProperty("unclassifiedRuntimePropertyCount")
            .GetInt32()
            .ShouldBe(0);

        string guide = File.ReadAllText(FindRepositoryFile(
            "docs", "developer-guides", "rendering", "poiyomi-toon-material-conversion.md"));
        guide.ShouldContain($"Poiyomi Toon {SourceToon93Catalog.VersionText}");
        guide.ShouldContain(SourceToon93Catalog.RepositoryCommit);

        foreach (FieldInfo field in typeof(MaterialConversionDiagnosticCodes).GetFields(
                     BindingFlags.Public | BindingFlags.Static))
        {
            string code = (string)field.GetRawConstantValue()!;
            guide.ShouldContain($"`{code}`");
        }
    }

    [Test]
    public void EveryCatalogPropertyHasAnExecutableSupportOutcome()
    {
        using JsonDocument catalog = OpenCatalog();
        foreach (JsonElement property in catalog.RootElement.GetProperty("properties").EnumerateArray())
        {
            string classification = property.GetProperty("classification").GetString()!;
            string parity = property.GetProperty("initialParity").GetString()!;
            classification.ShouldBeOneOf(
                "runtime", "integration", "renderState", "animationLocking",
                "compatibilityAlias", "inspectorOnly", "internalData");
            parity.ShouldBeOneOf("exact", "nativeEquivalent", "preservedInactive", "missing", "notApplicable");

            if (classification is "runtime" or "renderState" or "animationLocking")
                parity.ShouldNotBe("notApplicable");
        }
    }

    [Test]
    public void GeneratedParityTableListsEveryActiveAnnotationAndReachableWorkflow()
    {
        string report = File.ReadAllText(FindRepositoryFile(
            "docs", "reference", "rendering", "poiyomi-toon-9.3.64-parity.md"));
        using JsonDocument catalog = OpenCatalog();
        foreach (JsonElement annotation in catalog.RootElement.GetProperty("annotations").EnumerateArray())
        {
            if (annotation.GetProperty("activeUsageCount").GetInt32() > 0)
                report.ShouldContain($"`{annotation.GetProperty("name").GetString()}`");
        }
        foreach (JsonElement workflow in catalog.RootElement.GetProperty("workflows").EnumerateArray())
            report.ShouldContain($"`{workflow.GetProperty("id").GetString()}`");

        SourceToonAuthoringParityAudit.Unclassified.ShouldBeEmpty();
    }

    [Test]
    public void MaintenanceToolingRequiresCatalogFixtureAndValidationReview()
    {
        string audit = File.ReadAllText(FindRepositoryFile(
            "Tools", "Reports", "Test-PoiyomiSourceVersion.ps1"));
        audit.ShouldContain("properties");
        audit.ShouldContain("passes");
        audit.ShouldContain("annotations");
        audit.ShouldContain("workflows");
        audit.ShouldContain("Update parity fixtures");
        audit.ShouldContain("Validate-PoiyomiParity.ps1");

        string pullRequest = File.ReadAllText(FindRepositoryFile(
            ".github", "PULL_REQUEST_TEMPLATE.md"));
        pullRequest.ShouldContain("catalog diff");
        pullRequest.ShouldContain("native-equivalent behavior");
        pullRequest.ShouldContain("license/attribution review");
    }

    [Test]
    public void ConversionChecklistIsFullyClosed()
    {
        string checklist = File.ReadAllText(FindRepositoryFile(
            "docs", "work", "todo", "rendering", "poiyomi-toon-93-parity-checklist.md"));
        checklist.ShouldContain("- Status: Complete");
        checklist.ShouldNotContain("- [ ]");
    }

    [Test]
    public void SourceToonArtifactsUseFeatureOrientedNames()
    {
        string repository = Path.GetDirectoryName(FindRepositoryFile("XRENGINE.slnx"))!;
        string milestonePattern = @"\bph" + @"ase\s*[0-9]+\b|ph" + @"ase[0-9]+";
        Regex numberedMilestone = new(milestonePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string[] roots =
        [
            Path.Combine(repository, "Build", "CommonAssets", "Shaders", "Uber"),
            Path.Combine(repository, "XREngine.Editor", "Importers"),
            Path.Combine(repository, "XREngine.Editor", "MaterialAuthoring"),
            Path.Combine(repository, "XREngine.UnitTests", "Rendering"),
            Path.Combine(repository, "XREngine.UnitTests", "TestData", "Poiyomi"),
            Path.Combine(repository, "Tools"),
            Path.Combine(repository, "docs"),
        ];

        foreach (string path in roots.SelectMany(static root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                     .Where(static path =>
                         Path.GetFileName(path).Contains("poiyomi", StringComparison.OrdinalIgnoreCase) ||
                         path.Contains($"{Path.DirectorySeparatorChar}Poiyomi{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            numberedMilestone.IsMatch(Path.GetFileName(path)).ShouldBeFalse(path);
            string extension = Path.GetExtension(path);
            if (extension is not (".cs" or ".md" or ".ps1" or ".glsl" or ".frag" or ".yaml" or ".json"))
                continue;
            numberedMilestone.IsMatch(File.ReadAllText(path)).ShouldBeFalse(path);
        }
    }

    private static JsonDocument OpenCatalog()
        => JsonDocument.Parse(SourceToon93Catalog.OpenCatalog());

    private static string FindRepositoryFile(params string[] segments)
        => SourceToonParityCorpusTests.FindRepositoryFile(segments);
}
