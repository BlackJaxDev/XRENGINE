using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene.Importers;
using XREngine.Scene.Importers.Poiyomi;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class UnityPoiyomiProDowngradeTests
{
    private IRuntimeShaderServices? _previousShaderServices;
    private IRuntimeRenderingHostServices? _previousRenderingServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        _previousRenderingServices = RuntimeRenderingHostServices.Current;
        RuntimeShaderServices.Current = new UnityAvatarImportTestShaderServices();
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousShaderServices;
        RuntimeRenderingHostServices.Current = _previousRenderingServices!;
    }

    [TestCase("9.3.66", false, false)]
    [TestCase("9.3.11", true, false)]
    [TestCase("9.3.11", true, true)]
    public void Matcher_ClassifiesUnlockedLockedAndGrabPassProAsDowngradeOnly(
        string version,
        bool locked,
        bool grabPass)
    {
        string shaderName = locked
            ? grabPass
                ? "Hidden/Locked/Poiyomi Pro Grab Pass Synthetic"
                : "Hidden/Locked/Poiyomi Pro Synthetic"
            : ".poiyomi/Poiyomi Pro";
        string path = locked
            ? $"Assets/OptimizedShaders/{shaderName.Replace('/', '_')}.shader"
            : "Assets/_PoiyomiShaders/Shaders/9.3/Pro/Poiyomi Pro.shader";
        var properties = new HashSet<string>(StringComparer.Ordinal)
        {
            "_MainTex",
            "_ShadingEnabled",
            "_ShaderOptimizerEnabled",
            "shader_master_label",
            "shader_is_using_thry_editor",
        };
        if (grabPass)
            properties.Add("_GrabPass");

        PoiyomiShaderMatchResult match = PoiyomiShaderMatcher.Match(
            new PoiyomiShaderMatchInput
            {
                ShaderPath = path,
                ShaderSource =
                    $"Shader \"{shaderName}\" {{ // Poiyomi {version} " +
                    (locked ? "OPTIMIZER_ENABLED" : string.Empty) +
                    " }",
                PropertyNames = properties,
                OverrideTags = locked
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["OriginalShaderGUID"] = "abcdefabcdefabcdefabcdefabcdefab",
                    }
                    : new Dictionary<string, string>(StringComparer.Ordinal),
            });

        PoiyomiShaderVersion.TryParse(version, out PoiyomiShaderVersion expectedVersion)
            .ShouldBeTrue();
        match.Kind.ShouldBe(PoiyomiShaderMatchKind.PoiyomiProDowngradeSource);
        match.SourceFamily.ShouldBe(PoiyomiShaderFamily.Pro);
        match.Version.ShouldBe(expectedVersion);
        match.IsPoiyomiFamily.ShouldBeTrue();
        match.IsDowngradeSource.ShouldBeTrue();
        match.IsAccepted.ShouldBeFalse();
        match.IsLocked.ShouldBe(locked);
        match.Diagnostics.ShouldContain(static diagnostic =>
            diagnostic.Code == MaterialConversionDiagnosticCodes.ProLossyDowngrade);
    }

    [Test]
    public void Importer_NormalizesCommonSurfaceAndDropsActiveProOnlyGroups()
    {
        string materialPath = ResolveFixturePath(
            "Assets",
            "Materials",
            "LockedProSynthetic.mat");
        string projectRoot = ResolveFixturePath();

        UnityMaterialImportResult result =
            UnityMaterialImporter.ImportWithReport(materialPath, projectRoot);

        result.IsPoiyomiProDowngrade.ShouldBeTrue();
        result.IsPoiyomiToon.ShouldBeFalse();
        result.PoiyomiShaderMatch.ShouldNotBeNull().IsLocked.ShouldBeTrue();
        result.PoiyomiShaderMatch.Version.ShouldBe(new PoiyomiShaderVersion(9, 3, 11));
        result.ConversionReport.ShouldNotBeNull().Outcome
            .ShouldBe(EMaterialConversionOutcome.DowngradedToPoiyomiToon);

        XRMaterial material = result.Material.ShouldNotBeNull();
        material.Parameter<ShaderVector4>("_Color").ShouldNotBeNull().Value
            .ShouldBe(new System.Numerics.Vector4(0.8f, 0.4f, 0.2f, 0.65f));
        material.Parameter<ShaderVector4>("_MainTex_ST").ShouldNotBeNull().Value
            .ShouldBe(new System.Numerics.Vector4(2.0f, 3.0f, 0.25f, 0.5f));
        material.Parameter<ShaderFloat>("_EmissionStrength").ShouldNotBeNull().Value
            .ShouldBe(2.0f, 0.0001f);
        material.UberAuthoredState.GetFeature("emission")
            .ShouldNotBeNull().Enabled.ShouldBeTrue();
        material.TransparencyMode.ShouldBe(ETransparencyMode.PremultipliedAlpha);

        material.Parameter<ShaderFloat>("_GrabPass").ShouldBeNull();
        material.Parameter<ShaderFloat>("_RefractionEnabled").ShouldBeNull();
        material.Parameter<ShaderFloat>("_BlurStrength").ShouldBeNull();
        result.Diagnostics.Count(static diagnostic =>
                diagnostic.Code == MaterialConversionDiagnosticCodes.ProFeatureDiscarded)
            .ShouldBe(3);
        result.Diagnostics.Select(static diagnostic => diagnostic.Message)
            .ShouldContain(static message => message.Contains("Grab Pass", StringComparison.Ordinal));
        result.Diagnostics.Select(static diagnostic => diagnostic.Message)
            .ShouldContain(static message => message.Contains("Refraction", StringComparison.Ordinal));
        result.Diagnostics.Select(static diagnostic => diagnostic.Message)
            .ShouldContain(static message => message.Contains("Blur", StringComparison.Ordinal));
        result.ConversionReport.GeneratedFeatures.ShouldNotContain(static feature =>
            feature.Contains("grab", StringComparison.OrdinalIgnoreCase) ||
            feature.Contains("refract", StringComparison.OrdinalIgnoreCase) ||
            feature.Contains("blur", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveFixturePath(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            string root = Path.Combine(
                directory.FullName,
                "XREngine.UnitTests",
                "TestData",
                "UnityAvatarProject");
            string candidate = segments.Length == 0
                ? root
                : Path.Combine(root, Path.Combine(segments));
            if (segments.Length == 0 ? Directory.Exists(candidate) : File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the synthetic Unity avatar fixture.");
    }
}
