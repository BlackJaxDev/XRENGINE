using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene.Importers;
using XREngine.Scene.Importers.Poiyomi;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class PoiyomiImportReportingTests
{
    private IRuntimeShaderServices? _previousShaderServices;
    private IRuntimeRenderingHostServices? _previousRenderingServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        _previousRenderingServices = RuntimeRenderingHostServices.Current;
        RuntimeShaderServices.Current = new FileBackedRuntimeShaderServices();
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousShaderServices;
        RuntimeRenderingHostServices.Current = _previousRenderingServices!;
    }

    [Test]
    public void ImportReport_IsDeterministicAndDescribesSourceParity()
    {
        (string projectRoot, string materialPath) = CreateProject("Report", 2.0f);

        UnityMaterialImportResult result =
            UnityMaterialImporter.ImportWithReport(materialPath, projectRoot);

        MaterialConversionReport report = result.ConversionReport.ShouldNotBeNull();
        report.Outcome.ShouldBe(EMaterialConversionOutcome.Converted);
        report.SourceShaderFamily.ShouldBe("Poiyomi Toon");
        report.SourceShaderVersion.ShouldBe(PoiyomiToon93Catalog.VersionText);
        report.SourceWasLocked.ShouldBeFalse();
        report.ConverterVersion.ShouldBe(MaterialConversionReportBuilder.ConverterVersion);
        report.SourceDescriptorVersion.ShouldBe(MaterialConversionReportBuilder.SourceDescriptorVersion);
        report.Features.ShouldContain(feature =>
            feature.FeatureId == "emission" &&
            feature.Parity == EMaterialFeatureParity.Exact &&
            feature.SourceEnabled);
        report.GeneratedPasses.ShouldNotBeEmpty();
        report.Counters.GeneratedFeatures.ShouldBeGreaterThan(0);
        report.Counters.GeneratedVariants.ShouldBe(1);

        string json = report.ToJson();
        report.ToJson().ShouldBe(json);
        MaterialConversionReport.TryParse(json, out MaterialConversionReport? parsed).ShouldBeTrue();
        parsed.ShouldNotBeNull().SourceContentSha256.ShouldBe(report.SourceContentSha256);
        MaterialConversionReportRegistry.Instance.TryGet(
            result.Material.ShouldNotBeNull(),
            out MaterialConversionReport registered).ShouldBeTrue();
        registered.SourceContentSha256.ShouldBe(report.SourceContentSha256);
    }

    [Test]
    public void Reconvert_PreservesSeparatedOverrides_AndResetRestoresImportedValue()
    {
        (_, string materialPath) = CreateProject("Reimport", 2.0f);
        UnityMaterialAsset asset = new();
        asset.Import3rdParty(materialPath, null).ShouldBeTrue();
        string originalHash = asset.LastConversionReport.ShouldNotBeNull().SourceContentSha256;
        asset.ImportedState.ShouldNotBeNull().ConverterVersion
            .ShouldBe(MaterialConversionReportBuilder.ConverterVersion);

        asset.Parameter<ShaderFloat>("_EmissionStrength").ShouldNotBeNull().Value = 7.0f;
        WriteMaterial(materialPath, "Reimport", 3.0f);
        MaterialReimportWorkflow.NeedsReimport(asset, out string staleReason).ShouldBeTrue();
        staleReason.ShouldBe("The source Unity material content changed.");

        MaterialReimportWorkflow.Reconvert(asset, out UnityMaterialImportResult preserved).ShouldBeTrue();

        asset.Parameter<ShaderFloat>("_EmissionStrength").ShouldNotBeNull().Value.ShouldBe(7.0f);
        asset.LocalOverrides.Parameters.ShouldContainKey("_EmissionStrength");
        asset.ImportedState.ShouldNotBeNull().Parameters["_EmissionStrength"].Json.ShouldBe("3");
        preserved.ConversionReport.ShouldNotBeNull().SourceContentSha256.ShouldNotBe(originalHash);
        MaterialReimportWorkflow.NeedsReimport(asset, out _).ShouldBeFalse();

        MaterialReimportWorkflow.ResetAndReconvert(asset, out _).ShouldBeTrue();

        asset.Parameter<ShaderFloat>("_EmissionStrength").ShouldNotBeNull().Value.ShouldBe(3.0f);
        asset.LocalOverrides.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public async Task BatchAudit_IsSortedAggregatedAndMachineReadable()
    {
        (string projectRoot, _) = CreateProject("Zed", 1.0f);
        string materials = Path.Combine(projectRoot, "Assets", "Materials");
        string second = Path.Combine(materials, "Alpha.mat");
        WriteMaterial(second, "Alpha", 4.0f);

        UnityMaterialBatchReport first =
            await UnityMaterialBatchConversionService.AuditProjectAsync(projectRoot);
        UnityMaterialBatchReport secondRun =
            await UnityMaterialBatchConversionService.AuditProjectAsync(projectRoot);

        first.Materials.Count.ShouldBe(2);
        first.ConvertedMaterials.ShouldBe(2);
        first.FailedMaterials.ShouldBe(0);
        first.Materials.Select(static item => item.SourceAssetPath).ShouldBe(
            first.Materials.Select(static item => item.SourceAssetPath)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase));
        first.Counters.GeneratedFeatures.ShouldBe(
            first.Materials.Sum(static item => item.Report.Counters.GeneratedFeatures));
        first.ToJson().ShouldBe(secondRun.ToJson());
        first.ToJson().ShouldContain("\"ConverterVersion\"");
        first.ToJson().ShouldContain("\"SamplerPressure\"");
    }

    private static (string ProjectRoot, string MaterialPath) CreateProject(
        string materialName,
        float emissionStrength)
    {
        string projectRoot = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"poiyomi-reporting-{Guid.NewGuid():N}");
        string shaderPath = Path.Combine(
            projectRoot,
            "Assets",
            "_PoiyomiShaders",
            "Shaders",
            "9.3",
            "Toon",
            "Poiyomi Toon.shader");
        Directory.CreateDirectory(Path.GetDirectoryName(shaderPath)!);
        File.WriteAllText(shaderPath, "Shader \".poiyomi/Poiyomi Toon\" { // Poiyomi 9.3.64 }\n");
        File.WriteAllText(
            shaderPath + ".meta",
            $"fileFormatVersion: 2\nguid: {PoiyomiToon93Catalog.ShaderGuid}\n");

        string materialPath = Path.Combine(projectRoot, "Assets", "Materials", $"{materialName}.mat");
        WriteMaterial(materialPath, materialName, emissionStrength);
        return (projectRoot, materialPath);
    }

    private static void WriteMaterial(string path, string name, float emissionStrength)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  serializedVersion: 8
  m_Name: {{name}}
  m_Shader: {fileID: 4800000, guid: {{PoiyomiToon93Catalog.ShaderGuid}}, type: 3}
  m_CustomRenderQueue: -1
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs: []
    m_Ints: []
    m_Floats:
    - _EnableEmission: 1
    - _EmissionStrength: {{emissionStrength.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
    - _ShaderOptimizerEnabled: 0
    m_Colors:
    - _Color: {r: 1, g: 1, b: 1, a: 1}
""");
    }

    private sealed class FileBackedRuntimeShaderServices : IRuntimeShaderServices
    {
        public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
            => new T();

        public T LoadEngineAsset<T>(
            JobPriority priority,
            bool bypassJobThread,
            string assetRoot,
            string relativePath)
            where T : XRAsset, new()
            => CreateEngineAsset<T>(relativePath);

        public Task<T> LoadEngineAssetAsync<T>(
            JobPriority priority,
            bool bypassJobThread,
            string assetRoot,
            string relativePath)
            where T : XRAsset, new()
            => Task.FromResult(CreateEngineAsset<T>(relativePath));

        public void LogWarning(string message)
        {
        }

        private static T CreateEngineAsset<T>(string relativePath) where T : XRAsset, new()
        {
            if (typeof(T) != typeof(XRShader))
                return new T();

            string fullPath = ResolveWorkspacePath(
                Path.Combine("Build", "CommonAssets", "Shaders", relativePath));
            TextFile source = new(fullPath)
            {
                Text = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "void main() {}\n",
            };
            XRShader shader = new(XRShader.ResolveType(Path.GetExtension(relativePath)), source)
            {
                FilePath = fullPath,
                Name = Path.GetFileName(relativePath),
            };
            return (T)(XRAsset)shader;
        }

        private static string ResolveWorkspacePath(string relativePath)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                string candidate = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
            return relativePath;
        }
    }}
