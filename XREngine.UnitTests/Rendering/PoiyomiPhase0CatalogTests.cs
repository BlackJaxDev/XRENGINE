using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.Scene.Importers.Poiyomi;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class PoiyomiPhase0CatalogTests
{
    [Test]
    public void EmbeddedCatalog_AccountsForPinnedShaderAndActiveUiSurface()
    {
        using Stream stream = PoiyomiToon93Catalog.OpenCatalog();
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;

        JsonElement source = root.GetProperty("source");
        source.GetProperty("shaderVersion").GetString().ShouldBe(PoiyomiToon93Catalog.VersionText);
        source.GetProperty("commit").GetString().ShouldBe(PoiyomiToon93Catalog.RepositoryCommit);
        source.GetProperty("shaderGuid").GetString().ShouldBe(PoiyomiToon93Catalog.ShaderGuid);
        source.GetProperty("shaderBlob").GetString().ShouldBe(PoiyomiToon93Catalog.ShaderBlob);
        source.GetProperty("shaderSha256").GetString().ShouldBe(PoiyomiToon93Catalog.ShaderSha256);
        source.GetProperty("thryEditorTree").GetString().ShouldBe(PoiyomiToon93Catalog.ThryEditorTree);

        JsonElement summary = root.GetProperty("summary");
        summary.GetProperty("propertyCount").GetInt32().ShouldBe(3736);
        summary.GetProperty("texturePropertyCount").GetInt32().ShouldBe(137);
        summary.GetProperty("passCount").GetInt32().ShouldBe(5);
        summary.GetProperty("annotationKindCount").GetInt32().ShouldBe(41);
        summary.GetProperty("displayOptionKindCount").GetInt32().ShouldBe(27);
        summary.GetProperty("actionTypeCount").GetInt32().ShouldBe(2);
        summary.GetProperty("localizationKeyCount").GetInt32().ShouldBe(3501);
        summary.GetProperty("workflowCount").GetInt32().ShouldBeGreaterThanOrEqualTo(50);
        summary.GetProperty("unclassifiedRuntimePropertyCount").GetInt32().ShouldBe(0);

        string[] passNames = root.GetProperty("passes")
            .EnumerateArray()
            .Select(static pass => pass.GetProperty("name").GetString()!)
            .ToArray();
        passNames.ShouldBe(["EarlyZ", "Base", "Add", "Outline", "ShadowCaster"], ignoreOrder: true);
    }

    [Test]
    public void EmbeddedCatalog_HasUniqueClassifiedPropertiesAndCompleteAnnotationReferences()
    {
        using Stream stream = PoiyomiToon93Catalog.OpenCatalog();
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;

        JsonElement.ArrayEnumerator properties = root.GetProperty("properties").EnumerateArray();
        HashSet<string> propertyNames = new(StringComparer.Ordinal);
        HashSet<string> acceptedRuntimeParity = new(StringComparer.Ordinal)
        {
            "exact",
            "nativeEquivalent",
            "preservedInactive",
            "missing",
        };

        foreach (JsonElement property in properties)
        {
            string name = property.GetProperty("name").GetString()!;
            propertyNames.Add(name).ShouldBeTrue($"Duplicate catalog property '{name}'.");
            string classification = property.GetProperty("classification").GetString()!;
            classification.ShouldNotBeNullOrWhiteSpace();
            if (classification is "runtime" or "renderState" or "animationLocking")
            {
                string parity = property.GetProperty("initialParity").GetString()!;
                acceptedRuntimeParity.Contains(parity).ShouldBeTrue(
                    $"Property '{name}' has invalid initial parity '{parity}'.");
            }
        }

        foreach (JsonElement annotation in root.GetProperty("annotations").EnumerateArray())
        {
            annotation.GetProperty("activeUsageCount").GetInt32().ShouldBeGreaterThan(0);
            foreach (JsonElement propertyName in annotation.GetProperty("properties").EnumerateArray())
                propertyNames.Contains(propertyName.GetString()!).ShouldBeTrue();
        }

        foreach (string coverageName in new[] { "displayOptions", "actionTypes" })
        {
            foreach (JsonElement coverage in root.GetProperty(coverageName).EnumerateArray())
            {
                coverage.GetProperty("activeUsageCount").GetInt32().ShouldBeGreaterThan(0);
                coverage.GetProperty("source").GetString().ShouldNotBeNullOrWhiteSpace();
                foreach (JsonElement propertyName in coverage.GetProperty("properties").EnumerateArray())
                    propertyNames.Contains(propertyName.GetString()!).ShouldBeTrue();
            }
        }
    }

    [Test]
    public void Matcher_AcceptsOnlyPinnedVersionAndEmitsActionableUnknownVersionDiagnostic()
    {
        PoiyomiShaderMatchResult exact = PoiyomiShaderMatcher.Match(new PoiyomiShaderMatchInput
        {
            ShaderPath = "Assets/_PoiyomiShaders/Shaders/9.3/Toon/Poiyomi Toon.shader",
            ShaderSource = "Shader \".poiyomi/Poiyomi Toon\" { /* Poiyomi 9.3.64 */ }",
        });

        exact.IsAccepted.ShouldBeTrue();
        exact.Kind.ShouldBe(PoiyomiShaderMatchKind.ExactUnlockedSource);
        exact.Version.ShouldBe(PoiyomiToon93Catalog.Version);
        exact.Diagnostics.ShouldBeEmpty();

        PoiyomiShaderMatchResult unknown = PoiyomiShaderMatcher.Match(new PoiyomiShaderMatchInput
        {
            ShaderPath = "Assets/_PoiyomiShaders/Shaders/10.0/Toon/Poiyomi Toon.shader",
            ShaderSource = "Shader \".poiyomi/Poiyomi Toon\" { /* Poiyomi 10.0.1 */ }",
        });

        unknown.IsAccepted.ShouldBeFalse();
        unknown.IsPoiyomiFamily.ShouldBeTrue();
        unknown.Kind.ShouldBe(PoiyomiShaderMatchKind.UnsupportedVersion);
        unknown.Diagnostics.Single().Code.ShouldBe(MaterialConversionDiagnosticCodes.UnknownVersion);
        unknown.Diagnostics.Single().Message.ShouldContain("9.3.64");
    }

    [Test]
    public void Matcher_RecognizesLockedGuidAndWarnsForSignatureOnlyFallback()
    {
        PoiyomiShaderMatchResult tagged = PoiyomiShaderMatcher.Match(new PoiyomiShaderMatchInput
        {
            ShaderPath = "Assets/Materials/OptimizedShaders/Body/Locked.shader",
            OverrideTags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OriginalShaderGUID"] = PoiyomiToon93Catalog.ShaderGuid,
            },
        });

        tagged.IsAccepted.ShouldBeTrue();
        tagged.IsLocked.ShouldBeTrue();
        tagged.Kind.ShouldBe(PoiyomiShaderMatchKind.ExactLockedSource);

        HashSet<string> signature = new(StringComparer.Ordinal)
        {
            "shader_master_label",
            "shader_is_using_thry_editor",
            "_ShaderOptimizerEnabled",
            "_MainTex",
            "_ShadingEnabled",
        };
        PoiyomiShaderMatchResult signatureOnly = PoiyomiShaderMatcher.Match(new PoiyomiShaderMatchInput
        {
            ShaderPath = "Assets/Materials/OptimizedShaders/Body/Locked.shader",
            PropertyNames = signature,
        });

        signatureOnly.IsAccepted.ShouldBeTrue();
        signatureOnly.IsLocked.ShouldBeTrue();
        signatureOnly.Kind.ShouldBe(PoiyomiShaderMatchKind.LockedPropertySignature);
        signatureOnly.Diagnostics.Single().Code.ShouldBe(MaterialConversionDiagnosticCodes.AmbiguousLockedSignature);
    }

    [Test]
    public void GeneratedNames_AreStableAndFilesystemSafe()
    {
        PoiyomiToon93Catalog.GetMaterialName(
                @"Assets\Avatar Materials\Body!.mat",
                "ABCDEF0123456789abcdef0123456789")
            .ShouldBe("Body.poiyomi-9_3_64.abcdef01.uber");
        PoiyomiToon93Catalog.GetPassVariantName("Body Material", "outline inverse hull", 0x1234UL)
            .ShouldBe("Body-Material.outline-inverse-hull.0000000000001234");
        PoiyomiToon93Catalog.GetPreservedMetadataName("Body Material")
            .ShouldBe("Body-Material.poiyomi-source.json");
        PoiyomiToon93Catalog.GetAnimationBindingName("Body Material", "_Rim Width")
            .ShouldBe("Body-Material/_Rim-Width");
    }
}
