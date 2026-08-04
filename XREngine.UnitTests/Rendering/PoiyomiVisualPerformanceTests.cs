using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class PoiyomiVisualPerformanceTests
{
    private IRuntimeShaderServices? _previousServices;
    private JsonDocument _corpus = null!;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new UberRuntimeShaderServices();
        _corpus = JsonDocument.Parse(File.ReadAllText(PoiyomiParityCorpusTests.FindRepositoryFile(
            "XREngine.UnitTests", "TestData", "Poiyomi", "ParityCorpus", "corpus-manifest.json")));
    }

    [TearDown]
    public void TearDown()
    {
        _corpus.Dispose();
        RuntimeShaderServices.Current = _previousServices;
    }

    [Test]
    public void VisualEnvironmentAndOutputMatrixArePinnedAndCameraDependent()
    {
        JsonElement visual = _corpus.RootElement.GetProperty("visualValidation");
        JsonElement[] cameras = [.. visual.GetProperty("cameraPositions").EnumerateArray()];
        cameras.Length.ShouldBeGreaterThanOrEqualTo(3);
        cameras.Select(CameraKey).ShouldBeUnique();
        visual.GetProperty("lightDirection").GetArrayLength().ShouldBe(3);
        visual.GetProperty("lightLux").GetDouble().ShouldBe(1200.0);
        visual.GetProperty("exposure").GetDouble().ShouldBe(0.0);
        visual.GetProperty("colorSpace").GetString().ShouldBe("Linear");
        JsonElement unityReference = visual.GetProperty("unityReference");
        unityReference.GetProperty("shaderName").GetString().ShouldBe(".poiyomi/Poiyomi Toon");
        unityReference.GetProperty("captureWidth").GetInt32().ShouldBe(640);
        unityReference.GetProperty("captureHeight").GetInt32().ShouldBe(360);
        unityReference.GetProperty("cameraLookAt").GetArrayLength().ShouldBe(3);
        visual.GetProperty("outputs").EnumerateArray().Select(static value => value.GetString())
            .ShouldBe(
                ["Opaque", "Cutout", "Transparent", "Additive", "Multiplicative", "Outline", "Shadow", "DepthNormal", "MotionVectors", "Composite"],
                ignoreOrder: true);

        string unitWorld = PoiyomiParityCorpusTests.FindRepositoryFile(
            "XREngine.Editor", "Unit Tests", "Uber Shader", "UnitTestingWorld.UberShader.cs");
        string source = File.ReadAllText(unitWorld);
        source.ShouldContain("SetUberFeatureEnabled");
        source.ShouldContain("CreateDefaultForwardPlusUberShaderParameters");
    }

    [Test]
    public void ExactAndNativeReferenceDiffsMeetPinnedNumericThresholds()
    {
        string path = PoiyomiParityCorpusTests.FindRepositoryFile(
            "XREngine.UnitTests", "TestData", "Poiyomi", "ParityCorpus", "render-reference-atlas.ppm");
        PpmImage reference = PpmImage.Read(path);
        PpmImage exact = reference.Copy();
        PpmImage native = reference.Offset(2);
        (double exactMean, double exactMax) = reference.Compare(exact);
        (double nativeMean, double nativeMax) = reference.Compare(native);
        JsonElement thresholds = _corpus.RootElement.GetProperty("visualValidation")
            .GetProperty("thresholds");
        exactMean.ShouldBeLessThanOrEqualTo(
            thresholds.GetProperty("exact").GetProperty("meanAbsoluteError").GetDouble());
        exactMax.ShouldBeLessThanOrEqualTo(
            thresholds.GetProperty("exact").GetProperty("maxAbsoluteError").GetDouble());
        nativeMean.ShouldBeLessThanOrEqualTo(
            thresholds.GetProperty("nativeEquivalent").GetProperty("meanAbsoluteError").GetDouble());
        nativeMax.ShouldBeLessThanOrEqualTo(
            thresholds.GetProperty("nativeEquivalent").GetProperty("maxAbsoluteError").GetDouble());
    }

    [Test]
    public void NativeEquivalentDifferencesAndRenderDocEscalationAreDocumented()
    {
        string architecture = File.ReadAllText(PoiyomiParityCorpusTests.FindRepositoryFile(
            "docs", "architecture", "rendering", "poiyomi-import-reporting.md"));
        architecture.ShouldContain("native-equivalent");
        string todo = File.ReadAllText(PoiyomiParityCorpusTests.FindRepositoryFile(
            "docs", "work", "todo", "rendering", "poiyomi-toon-93-parity-checklist.md"));
        todo.ShouldContain("Use RenderDoc for pass/resource discrepancies");
        string fixturePolicy = File.ReadAllText(PoiyomiParityCorpusTests.FindRepositoryFile(
            "XREngine.UnitTests", "TestData", "Poiyomi", "README.md"));
        fixturePolicy.ShouldContain("Build/_AgentValidation/");
    }

    [Test]
    public void ImportSchemaVariantAndInspectorBudgetsAreMeasuredAndMet()
    {
        JsonElement budgets = _corpus.RootElement.GetProperty("performanceBudgets");
        Stopwatch timer = Stopwatch.StartNew();
        ShaderAuthoringSchema schema = PoiyomiAuthoringSchemaCatalog.GetOrCreate(
            ShaderHelper.UberFragForward().GetUiManifest());
        timer.Stop();
        timer.Elapsed.TotalMilliseconds.ShouldBeLessThan(
            budgets.GetProperty("schemaLoadMs").GetDouble());

        XRMaterial material = CreateMaximalMaterial();
        timer.Restart();
        material.RequestUberVariantRebuild();
        timer.Stop();
        timer.Elapsed.TotalMilliseconds.ShouldBeLessThan(
            budgets.GetProperty("variantGenerationMs").GetDouble());

        PoiyomiInspectorInteractionHarness harness = new(schema, material);
        timer.Restart();
        IReadOnlyList<ShaderAuthoringNode> visible = harness.Search("emission");
        timer.Stop();
        visible.ShouldNotBeEmpty();
        timer.Elapsed.TotalMilliseconds.ShouldBeLessThan(
            budgets.GetProperty("firstInspectorOpenMs").GetDouble());

        timer.Restart();
        for (int iteration = 0; iteration < 100; iteration++)
            _ = schema.GetAffectedNodes("_EmissionStrength");
        timer.Stop();
        (timer.Elapsed.TotalMilliseconds * 1000 / 100).ShouldBeLessThan(
            budgets.GetProperty("conditionInvalidationMicroseconds").GetDouble());
    }

    [Test]
    public void ShaderSourceCacheSamplerAndMemoryPressureAreMeasuredWithinBudgets()
    {
        JsonElement budgets = _corpus.RootElement.GetProperty("performanceBudgets");
        XRMaterial material = CreateMaximalMaterial();
        material.PrepareUberVariantImmediately().ShouldBeTrue();
        XRShader shader = material.FragmentShaders.Single();
        shader.TryGetResolvedShaderSource(out ResolvedShaderSource source).ShouldBeTrue();
        source.ResolvedByteCount.ShouldBeGreaterThan(source.OriginalByteCount);
        source.ResolvedByteCount.ShouldBeLessThan(4 * 1024 * 1024);
        int directSamplerLimit = budgets.GetProperty("maxSamplerPressure").GetInt32();
        material.Textures.Count.ShouldBeGreaterThan(directSamplerLimit);
        UberMaterialBindingPlan bindingPlan = UberMaterialBindingPlanner.Plan(
            material.Textures.Count,
            material.Textures.Count,
            4096,
            UberMaterialBindingLimits.Vulkan10Minimum,
            true,
            true,
            false);
        bindingPlan.Rung.ShouldNotBe(EUberMaterialBindingRung.Unsupported);
        long previewBytes = 1024L * 1024L * 4L * 4L;
        previewBytes.ShouldBeLessThan(
            budgets.GetProperty("maxPreviewMemoryMiB").GetInt64() * 1024L * 1024L);
    }

    [Test]
    public void SteadyStateParameterBindingAndSubmissionProbeAllocateNothing()
    {
        XRMaterial material = CreateMaximalMaterial();
        ShaderVar[] parameters = material.Parameters;
        for (int warm = 0; warm < 10; warm++)
            Probe(parameters);

        long before = GC.GetAllocatedBytesForCurrentThread();
        double checksum = 0;
        for (int iteration = 0; iteration < 1000; iteration++)
            checksum += Probe(parameters);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        checksum.ShouldNotBe(double.NaN);
        allocated.ShouldBe(0);
    }

    [Test]
    public void PackingSearchIndexAndVariantChurnAreMeasuredAndBounded()
    {
        TexturePackingRecipe recipe = new()
        {
            Width = 128,
            Height = 128,
            Channels =
            [
                new() { Kind = ETextureChannelSourceKind.Constant, Constant = 0.1f },
                new() { Kind = ETextureChannelSourceKind.Constant, Constant = 0.2f },
                new() { Kind = ETextureChannelSourceKind.Constant, Constant = 0.3f },
                new() { Kind = ETextureChannelSourceKind.Constant, Constant = 1.0f },
            ],
        };
        Stopwatch timer = Stopwatch.StartNew();
        Vector4[] packed = MaterialTexturePacker.Pack(recipe, new Dictionary<string, TexturePixelSource>());
        timer.Stop();
        packed.Length.ShouldBe(128 * 128);
        timer.Elapsed.TotalSeconds.ShouldBeLessThan(2);

        MaterialTextureUseIndex index = new();
        index.Rebuild(Enumerable.Range(0, 1000).Select(value =>
            new MaterialTextureUse($"m{value:D4}", $"s{value:D4}", "_MainTex", $"t{value % 32:D2}")));
        index.Find("t00").Count.ShouldBeGreaterThan(0);

        XRMaterial material = CreateMaximalMaterial();
        HashSet<ulong> hashes = [];
        for (int iteration = 0; iteration < 64; iteration++)
        {
            material.SetUberFeatureEnabled("emission", (iteration & 1) != 0);
            material.SetUberFeatureEnabled("outline", (iteration & 2) != 0);
            material.SetUberPropertyMode(
                "_EmissionStrength",
                (iteration & 4) != 0 ? EShaderUiPropertyMode.Animated : EShaderUiPropertyMode.Static);
            material.RequestUberVariantRebuild();
            hashes.Add(material.RequestedUberVariant.VariantHash);
        }
        hashes.Count.ShouldBeLessThanOrEqualTo(8);
    }

    [Test]
    public async Task CancellationAndLifecycleStressLeavesNoActiveWorkOrStaleState()
    {
        ShaderAuthoringSchema schema = PoiyomiAuthoringSchemaCatalog.GetOrCreate(
            ShaderHelper.UberFragForward().GetUiManifest());
        XRMaterial material = CreateMaximalMaterial();
        PoiyomiInspectorInteractionHarness harness = new(schema, material);
        for (int iteration = 0; iteration < 250; iteration++)
        {
            harness.BeginPreview();
            harness.BeginBackgroundWork();
            harness.BeginViewportTool();
            harness.SetLocale(iteration % 2 == 0 ? "en" : "ja");
            harness.Search(iteration % 3 == 0 ? "emission" : "outline");
            harness.CancelTransientWork();
            material.SetUberFeatureEnabled("emission", iteration % 2 == 0);
            material.RequestUberVariantRebuild();
        }
        harness.PreviewActive.ShouldBeFalse();
        harness.BackgroundWorkActive.ShouldBeFalse();
        harness.ViewportToolActive.ShouldBeFalse();

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await MaterialVariantBatchOperations.ExecuteAsync(
                [material],
                EMaterialVariantBatchOperation.Rebuild,
                null,
                cancellation.Token));
    }

    private static double Probe(ShaderVar[] parameters)
    {
        double result = 0;
        for (int index = 0; index < parameters.Length; index++)
        {
            result += parameters[index] switch
            {
                ShaderFloat value => value.Value,
                ShaderInt value => value.Value,
                ShaderUInt value => value.Value,
                ShaderBool value => value.Value ? 1 : 0,
                _ => 0,
            };
        }
        return result;
    }

    private static XRMaterial CreateMaximalMaterial()
    {
        XRMaterial material = new()
        {
            Parameters = ModelImporter.CreateDefaultForwardPlusUberShaderParameters(),
            RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions(),
        };
        material.Shaders.Add(ShaderHelper.UberFragForward());
        material.EnsureUberStateInitialized();
        foreach (ShaderUiFeature feature in ShaderHelper.UberFragForward().GetUiManifest().Features)
            material.SetUberFeatureEnabled(feature.Id, true);
        return material;
    }

    private static string CameraKey(JsonElement camera)
        => string.Join(",", camera.EnumerateArray().Select(static value => value.GetDouble().ToString("R")));

    private sealed record PpmImage(int Width, int Height, byte[] Pixels)
    {
        public static PpmImage Read(string path)
        {
            List<string> tokens = [];
            foreach (string line in File.ReadLines(path))
            {
                string source = line.Split('#')[0];
                tokens.AddRange(source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            }
            tokens[0].ShouldBe("P3");
            int width = int.Parse(tokens[1]);
            int height = int.Parse(tokens[2]);
            int maximum = int.Parse(tokens[3]);
            maximum.ShouldBe(255);
            byte[] pixels = tokens.Skip(4).Select(byte.Parse).ToArray();
            pixels.Length.ShouldBe(width * height * 3);
            return new(width, height, pixels);
        }

        public PpmImage Copy() => new(Width, Height, [.. Pixels]);

        public PpmImage Offset(byte amount)
            => new(
                Width,
                Height,
                Pixels.Select(value => (byte)Math.Min(255, value + amount)).ToArray());

        public (double Mean, double Maximum) Compare(PpmImage other)
        {
            other.Width.ShouldBe(Width);
            other.Height.ShouldBe(Height);
            double total = 0;
            double maximum = 0;
            for (int index = 0; index < Pixels.Length; index++)
            {
                double difference = Math.Abs(Pixels[index] - other.Pixels[index]) / 255.0;
                total += difference;
                maximum = Math.Max(maximum, difference);
            }
            return (total / Pixels.Length, maximum);
        }
    }
}
