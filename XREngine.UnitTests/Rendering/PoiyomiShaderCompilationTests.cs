using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Generator;
using XREngine.Rendering.Vulkan;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class PoiyomiShaderCompilationTests
{
    private IRuntimeShaderServices? _previousServices;
    private IRuntimeRenderingHostServices? _previousHost;

    private static readonly string[] FeatureUniverse =
    [
        "normal-map", "stylized-shading", "alpha-masks", "color-adjustments",
        "material-ao", "shadow-masks", "emission", "matcap", "rim-lighting",
        "advanced-specular", "detail-textures", "outline", "backface", "glitter",
        "flipbook", "subsurface", "dissolve", "parallax", "poiyomi-surface",
        "poiyomi-masks-themes", "poiyomi-lighting-parity", "poiyomi-pbr-parity",
        "poiyomi-matcap-rim-slots", "poiyomi-decals", "poiyomi-emission-slots",
        "poiyomi-flipbook-array", "poiyomi-special-effects", "poiyomi-vertex-effects",
        "poiyomi-audiolink", "poiyomi-environment-adapters", "poiyomi-view-context",
    ];

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        _previousHost = RuntimeRenderingHostServices.Current;
        RuntimeShaderServices.Current = new PoiyomiRuntimeShaderServices();
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousServices;
        RuntimeRenderingHostServices.Current = _previousHost!;
    }

    [TestCase("minimal")]
    [TestCase("common")]
    [TestCase("family-maximal")]
    [TestCase("global-maximal")]
    public void RepresentativeVariants_CompileToWarningFreeSpirv(string profile)
    {
        string[] features = profile switch
        {
            "minimal" => [],
            "common" => ["normal-map", "stylized-shading", "emission"],
            "family-maximal" =>
            [
                "normal-map", "stylized-shading", "advanced-specular", "emission",
                "matcap", "rim-lighting", "detail-textures", "dissolve", "parallax",
                "poiyomi-decals", "poiyomi-special-effects", "poiyomi-vertex-effects",
            ],
            _ => FeatureUniverse,
        };
        XRMaterial material = CreateMaterial(features);
        material.PrepareUberVariantImmediately().ShouldBeTrue();
        XRShader shader = material.FragmentShaders.Single();
        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewritten);
        entryPoint.ShouldBe("main");
        spirv.Length.ShouldBeGreaterThan(20);
        rewritten.ShouldNotBeNullOrWhiteSpace();
        rewritten.ShouldContain("#define XRENGINE_VULKAN 1");
        rewritten.ShouldNotContain("warning:", Case.Insensitive);
    }

    [Test]
    public void PassPermutationContractsCoverAllRequiredSemanticPasses()
    {
        EMaterialPassIdentity[] required =
        [
            EMaterialPassIdentity.Base,
            EMaterialPassIdentity.EarlyDepth,
            EMaterialPassIdentity.DepthNormal,
            EMaterialPassIdentity.Shadow,
            EMaterialPassIdentity.Velocity,
            EMaterialPassIdentity.Picking,
            EMaterialPassIdentity.Outline,
        ];
        required.ShouldBeUnique();
        string[] shaderFiles =
        [
            "UberShader.frag", "UberShader.vert", "outline.frag", "outline.vert",
        ];
        foreach (string file in shaderFiles)
            File.Exists(PoiyomiParityCorpusTests.FindRepositoryFile(
                "Build", "CommonAssets", "Shaders", "Uber", file)).ShouldBeTrue();

        XRMaterial material = CreateMaterial(FeatureUniverse);
        material.PrepareUberVariantImmediately().ShouldBeTrue();
        foreach (EMaterialPassIdentity pass in required)
        {
            ulong passKey = unchecked((ulong)HashCode.Combine(material.RequestedUberVariant.VariantHash, pass));
            passKey.ShouldNotBe(0ul);
        }
    }

    [TestCase("UberShader.vert", "standard")]
    [TestCase("UberShader_OVR.vert", "OpenVR")]
    [TestCase("UberShader.vert", "OpenXR multiview")]
    public void StandardOpenVrAndOpenXrVertexVariantsCompile(string fileName, string profile)
    {
        XRShader vertex = ShaderHelper.LoadEngineShader(
            Path.Combine("Uber", fileName),
            EShaderType.Vertex);
        byte[] spirv = VulkanShaderCompiler.Compile(vertex, out string entryPoint, out _, out string? rewritten);
        spirv.Length.ShouldBeGreaterThan(20, profile);
        entryPoint.ShouldBe("main");
        rewritten.ShouldNotBeNullOrWhiteSpace();
        rewritten.ShouldNotContain("warning:", Case.Insensitive);
    }

    [Test]
    public void OpenGlAndVulkanPathsResolveTheSameFeatureSourceContract()
    {
        XRMaterial material = CreateMaterial(FeatureUniverse);
        material.PrepareUberVariantImmediately().ShouldBeTrue();
        XRShader fragment = material.FragmentShaders.Single();
        fragment.TryGetResolvedShaderSource(out ResolvedShaderSource openGlSource).ShouldBeTrue();
        VulkanShaderCompiler.PreparedSource vulkan =
            VulkanShaderCompiler.Prepare(fragment, useVulkanClipDepthRemap: true);

        openGlSource.ResolvedSource.ShouldContain("poiApplySurfaceEffects");
        vulkan.RewrittenSource.ShouldContain("poiApplySurfaceEffects");
        openGlSource.ResolvedSource.ShouldNotContain("#define XRENGINE_VULKAN 1");
        vulkan.RewrittenSource.ShouldContain("#define XRENGINE_VULKAN 1");
        openGlSource.MacroSummary.Defines.ShouldContain("XRENGINE_POIYOMI_EFFECT_FEATURES_GLSL");
    }

    [Test]
    public void NoForwardLightingVariant_KeepsNeutralFeatureContextAndCompiles()
    {
        XRShader fragment = ShaderHelper.UberFragForward();
        fragment.TryGetResolvedShaderSource(out ResolvedShaderSource resolved).ShouldBeTrue();
        string source = resolved.ResolvedSource.Replace(
            "#version 460",
            "#version 460\n#define XRENGINE_UBER_DISABLE_FORWARD_LIGHTING 1",
            StringComparison.Ordinal);
        source.ShouldContain("ToonLight light = createToonLight(");

        XRShader noForwardLighting = new(EShaderType.Fragment, source);
        byte[] spirv = VulkanShaderCompiler.Compile(
            noForwardLighting,
            out string entryPoint,
            out _,
            out string? rewritten);

        spirv.Length.ShouldBeGreaterThan(20);
        entryPoint.ShouldBe("main");
        rewritten.ShouldNotBeNullOrWhiteSpace();
        rewritten.ShouldNotContain("undefined variable", Case.Insensitive);
        rewritten.ShouldNotContain("warning:", Case.Insensitive);
    }

    [Test]
    public void GeneratedVertexShader_PoiyomiDeclarationsAreGlobalAndCompilesWarningFree()
    {
        XRMesh mesh = new(
            [
                new Vertex(Vector3.Zero, Vector2.Zero),
                new Vertex(Vector3.UnitX, Vector2.UnitX),
                new Vertex(Vector3.UnitY, Vector2.UnitY),
            ],
            [0, 1, 2]);
        DefaultVertexShaderGenerator generator = new(mesh);
        string source = generator.Generate();
        int mainIndex = source.IndexOf("void main()", StringComparison.Ordinal);

        mainIndex.ShouldBeGreaterThan(0);
        source.IndexOf("uniform float EngineTime;", StringComparison.Ordinal).ShouldBeLessThan(mainIndex);
        source.IndexOf("uniform float _PoiVertexEffectsEnabled;", StringComparison.Ordinal).ShouldBeLessThan(mainIndex);
        source.LastIndexOf("uniform float _PoiVertexEffectsEnabled;", StringComparison.Ordinal)
            .ShouldBe(source.IndexOf("uniform float _PoiVertexEffectsEnabled;", StringComparison.Ordinal));

        XRShader shader = new(EShaderType.Vertex, source);
        byte[] spirv = VulkanShaderCompiler.Compile(shader, out string entryPoint, out _, out string? rewritten);
        spirv.Length.ShouldBeGreaterThan(20);
        entryPoint.ShouldBe("main");
        rewritten.ShouldNotBeNullOrWhiteSpace();
        rewritten.ShouldNotContain("warning:", Case.Insensitive);
    }
    [Test]
    public void PairwiseFeatureSamplingProducesDeterministicCoverageWithoutDuplicatePairs()
    {
        List<(string Left, string Right, ulong Hash)> pairs = [];
        for (int left = 0; left < FeatureUniverse.Length; left++)
        for (int right = left + 1; right < FeatureUniverse.Length; right++)
        {
            string leftFeature = FeatureUniverse[left];
            string rightFeature = FeatureUniverse[right];
            ulong coverageHash = ComputePairCoverageHash(leftFeature, rightFeature);
            if (pairs.Count % 155 == 0)
            {
                XRMaterial sampled = CreateMaterial([leftFeature, rightFeature]);
                sampled.PrepareUberVariantImmediately().ShouldBeTrue();
                sampled.RequestedUberVariant.VariantHash.ShouldNotBe(0ul);
            }
            pairs.Add((leftFeature, rightFeature, coverageHash));
        }

        pairs.Count.ShouldBe(FeatureUniverse.Length * (FeatureUniverse.Length - 1) / 2);
        pairs.Select(static pair => $"{pair.Left}|{pair.Right}").ShouldBeUnique();
        pairs.ShouldAllBe(static pair => pair.Hash != 0);
    }

    private static ulong ComputePairCoverageHash(string left, string right)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (char value in left)
            hash = (hash ^ value) * prime;
        hash = (hash ^ (byte)'|') * prime;
        foreach (char value in right)
            hash = (hash ^ value) * prime;
        return hash;
    }

    private static XRMaterial CreateMaterial(IEnumerable<string> features)
    {
        XRMaterial material = new()
        {
            Parameters = ModelImporter.CreateDefaultForwardPlusUberShaderParameters(),
            RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions(),
        };
        material.Shaders.Add(ShaderHelper.UberFragForward());
        material.EnsureUberStateInitialized();
        foreach (string feature in features)
            material.SetUberFeatureEnabled(feature, true);
        return material;
    }
}


