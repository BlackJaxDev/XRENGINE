using System.Globalization;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ModelCacheVariantFingerprintTests
{
    [Test]
    public void Compute_IsStableLowercaseSha256Prefix_AndBuildIdentityIsDiagnosticOnly()
    {
        string sourcePath = CreateSourcePath("stable.gltf");
        ModelImportOptions options = new();
        ModelImportBackendResolution resolution = Resolve(sourcePath, options);

        ModelCacheVariantFingerprint first = ModelCacheVariantFingerprintBuilder.Compute(
            sourcePath,
            options,
            resolution,
            engineBuildIdentity: "build-a");
        ModelCacheVariantFingerprint second = ModelCacheVariantFingerprintBuilder.Compute(
            sourcePath,
            options,
            resolution,
            engineBuildIdentity: "build-b");

        first.Value.Length.ShouldBe(32);
        first.Value.All(static value => value is >= '0' and <= '9' or >= 'a' and <= 'f')
            .ShouldBeTrue();
        first.FullHash.Length.ShouldBe(64);
        first.FullHash.ShouldStartWith(first.Value);
        second.Value.ShouldBe(first.Value);
        second.FullHash.ShouldBe(first.FullHash);
        second.CanonicalBytes.ToArray().ShouldBe(first.CanonicalBytes.ToArray());
        first.EngineBuildIdentity.ShouldBe("build-a");
        second.EngineBuildIdentity.ShouldBe("build-b");
    }

    [Test]
    public void Compute_ExcludesExecutionOptionsAndProjectAuthoritativeRemaps()
    {
        string sourcePath = CreateSourcePath("execution-options.gltf");
        ModelImportOptions baseline = new();
        ModelImportOptions executionOnlyChanges = new()
        {
            MultiThread = false,
            NativeFbxMeshBuildMaxDegreeOfParallelism = 7,
            ProcessMeshesAsynchronously = true,
            GenerateMeshRenderersAsync = false,
            BatchSubmeshAddsDuringAsyncImport = false,
            ProgressCallback = static _ => { },
            TextureRemap = new Dictionary<string, XRTexture2D?>
            {
                ["BaseColor"] = new XRTexture2D(),
            },
            MaterialRemap = new Dictionary<string, XRMaterial?>
            {
                ["Body"] = new XRMaterial(),
            },
            LegacyTexturePathRemap = new Dictionary<string, string>
            {
                ["old-texture"] = "replacement-texture",
            },
            LegacyMaterialNameRemap = new Dictionary<string, string>
            {
                ["old-material"] = "replacement-material",
            },
        };

        ModelCacheVariantFingerprint baselineFingerprint = Compute(sourcePath, baseline);
        ModelCacheVariantFingerprint changedFingerprint = Compute(sourcePath, executionOnlyChanges);

        changedFingerprint.Value.ShouldBe(baselineFingerprint.Value);
        changedFingerprint.CanonicalBytes.ToArray()
            .ShouldBe(baselineFingerprint.CanonicalBytes.ToArray());
    }

    [Test]
    public void Compute_ChangesForOutputSettingsBackendCookPolicyOverridesAndCallerVariant()
    {
        string sourcePath = CreateSourcePath("semantic-options.gltf");
        ModelImportOptions baseline = new();
        string baselineFingerprint = Compute(sourcePath, baseline).Value;

        ModelImportOptions importChange = new()
        {
            ScaleConversion = 2.0f,
        };
        Compute(sourcePath, importChange).Value.ShouldNotBe(baselineFingerprint);

        ModelImportOptions backendChange = new()
        {
            GltfBackend = GltfImportBackend.Assimp,
        };
        Compute(sourcePath, backendChange).Value.ShouldNotBe(baselineFingerprint);

        ModelImportOptions cookChange = new();
        cookChange.CookSettings.Meshlets.Enabled = true;
        Compute(sourcePath, cookChange).Value.ShouldNotBe(baselineFingerprint);

        MeshOptimizerSubMeshSettings authoredSettings = new();
        authoredSettings.Lods.Enabled = true;
        ModelCookOverrideSnapshot overrides = new(
        [
            new ModelCookOverrideEntry(
                new ImportedEntityKey("gltf:mesh:0:primitive:0", isStable: false),
                authoredSettings),
        ]);
        Compute(sourcePath, baseline, overrides).Value.ShouldNotBe(baselineFingerprint);

        Compute(sourcePath, baseline, callerVariantKey: "preview").Value
            .ShouldNotBe(baselineFingerprint);
    }

    [Test]
    [NonParallelizable]
    public void Compute_IsIndependentOfCurrentCulture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            string sourcePath = CreateSourcePath(Path.Combine("IDENTITY", "Istanbul.gltf"));
            ModelImportOptions options = new()
            {
                ScaleConversion = 1.25f,
                UnityProjectRootOverride = "ImporterRoot",
                TextureLoadDirSearchPaths = ["Textures"],
            };

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            ModelCacheVariantFingerprint turkish = Compute(sourcePath, options);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            ModelCacheVariantFingerprint french = Compute(sourcePath, options);

            french.Value.ShouldBe(turkish.Value);
            french.CanonicalBytes.ToArray().ShouldBe(turkish.CanonicalBytes.ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Test]
    public void Compute_RejectsNonFiniteOutputSettings()
    {
        string sourcePath = CreateSourcePath("invalid.gltf");
        ModelImportOptions options = new()
        {
            ScaleConversion = float.NaN,
        };

        Should.Throw<ArgumentOutOfRangeException>(() => Compute(sourcePath, options));
    }

    private static ModelCacheVariantFingerprint Compute(
        string sourcePath,
        ModelImportOptions options,
        ModelCookOverrideSnapshot? overrides = null,
        string? callerVariantKey = null)
        => ModelCacheVariantFingerprintBuilder.Compute(
            sourcePath,
            options,
            Resolve(sourcePath, options),
            overrides,
            callerVariantKey);

    private static ModelImportBackendResolution Resolve(
        string sourcePath,
        ModelImportOptions options)
        => ModelImportBackendResolver.Resolve(
            sourcePath,
            options,
            preferredFbxBackend: FbxImportBackend.Auto,
            preferredGltfBackend: GltfImportBackend.Auto);

    private static string CreateSourcePath(string relativePath)
        => Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "ModelCacheFingerprint",
            relativePath));
}
