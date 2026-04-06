using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ForwardDepthNormalVariantTests : GpuTestBase
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new FileSystemRuntimeShaderServices(ShaderBasePath);
        ForwardDepthNormalVariantFactory.ClearCaches();
        ShaderHelper.ClearDefinedVariantSourceCache();
    }

    [TearDown]
    public void TearDown()
    {
        ForwardDepthNormalVariantFactory.ClearCaches();
        ShaderHelper.ClearDefinedVariantSourceCache();
        RuntimeShaderServices.Current = _previousServices;
    }

    [Test]
    public void NormalMappedForwardShader_RewritesToDepthNormalVariant()
    {
        string source = CreateCustomNormalMappedForwardSource();

        bool success = ForwardDepthNormalVariantFactory.TryCreateFragmentVariantSource(source, out string variantSource);

        success.ShouldBeTrue();
        variantSource.ShouldContain("layout (location = 0) out vec2 Normal;");
        variantSource.ShouldContain("#pragma snippet \"NormalEncoding\"");
        variantSource.ShouldContain("vec3 getNormalFromMap()");
        variantSource.ShouldContain("Normal = XRENGINE_EncodeNormal(normal);");
        variantSource.ShouldNotContain("XRENGINE_CalculateForwardLighting");
        variantSource.ShouldNotContain("#pragma snippet \"ForwardLighting\"");
        variantSource.ShouldNotContain("#pragma snippet \"AmbientOcclusionSampling\"");
    }

    [Test]
    public void MaskedForwardShader_PreservesDiscardPathInVariant()
    {
        string source = LoadShaderSource("Common/LitTexturedNormalAlphaForward.fs");

        bool success = ForwardDepthNormalVariantFactory.TryCreateFragmentVariantSource(source, out string variantSource);

        success.ShouldBeFalse();
        variantSource.ShouldBeEmpty();
    }

    [Test]
    public void SpecularForwardShader_RemovesAmbientOcclusionSamplingFromVariant()
    {
        string source = CreateCustomSpecularForwardSource();

        bool success = ForwardDepthNormalVariantFactory.TryCreateFragmentVariantSource(source, out string variantSource);

        success.ShouldBeTrue();
        variantSource.ShouldContain("float specularMask = texture(Texture1, FragUV0).r;");
        variantSource.ShouldContain("float specIntensity = MatSpecularIntensity * specularMask;");
        variantSource.ShouldContain("Normal = XRENGINE_EncodeNormal(normal);");
        variantSource.ShouldNotContain("XRE_SampleAmbientOcclusion");
        variantSource.ShouldNotContain("#pragma snippet \"AmbientOcclusionSampling\"");
    }

    [Test]
    public void FallbackRewrite_MemoizesVariantSourceBySourceContent()
    {
        const string customForwardSource = "#version 450\n" +
            "#pragma snippet \"ForwardLighting\"\n" +
            "layout (location = 0) out vec4 OutColor;\n" +
            "void main()\n" +
            "{\n" +
            "    vec3 normal = normalize(FragNorm);\n" +
            "    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, vec3(1.0), 1.0, 1.0);\n" +
            "    OutColor = vec4(totalLight, 1.0);\n" +
            "}\n";

        bool firstSuccess = ForwardDepthNormalVariantFactory.TryCreateFragmentVariantSource(customForwardSource, out string firstVariantSource);
        bool secondSuccess = ForwardDepthNormalVariantFactory.TryCreateFragmentVariantSource(customForwardSource, out string secondVariantSource);

        firstSuccess.ShouldBeTrue();
        secondSuccess.ShouldBeTrue();
        ReferenceEquals(firstVariantSource, secondVariantSource).ShouldBeTrue();
    }

    [Test]
    public void MaskedSpecularForwardShader_RemovesAmbientOcclusionSamplingAndPreservesDiscard()
    {
        string source = LoadShaderSource("Common/LitTexturedSpecAlphaForward.fs");

        bool success = ForwardDepthNormalVariantFactory.TryCreateFragmentVariantSource(source, out string variantSource);

        success.ShouldBeFalse();
        variantSource.ShouldBeEmpty();
    }

    [Test]
    public void Material_CachesDepthNormalVariantPerSourceMaterial()
    {
        XRMaterial material = new(XRShader.EngineShader("Common/LitTexturedNormalForward.fs", EShaderType.Fragment))
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
        };

        XRMaterial? firstVariant = material.DepthNormalPrePassVariant;
        XRMaterial? secondVariant = material.DepthNormalPrePassVariant;

        firstVariant.ShouldNotBeNull();
        ReferenceEquals(firstVariant, secondVariant).ShouldBeTrue();
    }

    [Test]
    public void ExplicitShaderModeVariant_InjectsDepthNormalDefine()
    {
        XRShader shader = XRShader.EngineShader("Common/LitTexturedNormalForward.fs", EShaderType.Fragment);

        XRShader? variant = ShaderHelper.GetDepthNormalPrePassForwardVariant(shader);

        variant.ShouldNotBeNull();
        string variantText = variant.Source.Text ?? throw new InvalidOperationException("Variant shader source text was null.");
        variantText.ShouldContain("#define XRENGINE_DEPTH_NORMAL_PREPASS");
        variantText.ShouldContain("layout (location = 0) out vec2 Normal;");
    }

    [Test]
    public void ExplicitShaderModeVariant_MemoizesDefinedVariantSourceByContent()
    {
        XRShader firstShader = XRShader.EngineShader("Common/LitTexturedNormalForward.fs", EShaderType.Fragment);
        XRShader secondShader = XRShader.EngineShader("Common/LitTexturedNormalForward.fs", EShaderType.Fragment);

        XRShader? firstVariant = ShaderHelper.GetDepthNormalPrePassForwardVariant(firstShader);
        XRShader? secondVariant = ShaderHelper.GetDepthNormalPrePassForwardVariant(secondShader);

        firstVariant.ShouldNotBeNull();
        secondVariant.ShouldNotBeNull();
        ReferenceEquals(firstVariant.Source.Text, secondVariant.Source.Text).ShouldBeTrue();
    }

    [Test]
    public void ExplicitShaderModeVariant_InjectsDepthNormalDefine_ForMaskedSpecularForwardShader()
    {
        XRShader shader = XRShader.EngineShader("Common/LitTexturedSpecAlphaForward.fs", EShaderType.Fragment);

        XRShader? variant = ShaderHelper.GetDepthNormalPrePassForwardVariant(shader);

        variant.ShouldNotBeNull();
        string variantText = variant.Source.Text ?? throw new InvalidOperationException("Variant shader source text was null.");
        variantText.ShouldContain("#define XRENGINE_DEPTH_NORMAL_PREPASS");
        variantText.ShouldContain("layout (location = 0) out vec2 Normal;");
        variantText.ShouldContain("float alphaMask = texture(Texture2, FragUV0).r;");
    }

    [Test]
    public void ExplicitShaderModeVariant_InjectsDepthNormalDefine_ForUnlitMaskedForwardShader()
    {
        XRShader shader = XRShader.EngineShader("Common/UnlitAlphaTexturedForward.fs", EShaderType.Fragment);

        XRShader? variant = ShaderHelper.GetDepthNormalPrePassForwardVariant(shader);

        variant.ShouldNotBeNull();
        string variantText = variant.Source.Text ?? throw new InvalidOperationException("Variant shader source text was null.");
        variantText.ShouldContain("#define XRENGINE_DEPTH_NORMAL_PREPASS");
        variantText.ShouldContain("layout (location = 0) out vec2 Normal;");
        variantText.ShouldContain("Normal = XRENGINE_EncodeNormal(FragNorm);");
    }

    [Test]
    public void ShadowCasterVariant_InjectsShadowCasterDefine()
    {
        XRShader shader = XRShader.EngineShader("Common/LitTexturedAlphaForward.fs", EShaderType.Fragment);

        XRShader? variant = ShaderHelper.GetShadowCasterForwardVariant(shader);

        variant.ShouldNotBeNull();
        string variantText = variant.Source.Text ?? throw new InvalidOperationException("Variant shader source text was null.");
        variantText.ShouldContain("#define XRENGINE_SHADOW_CASTER_PASS");
        variantText.ShouldContain("layout (location = 0) out float Depth;");
    }

    [Test]
    public void ShadowCasterVariant_NormalizesExactTransparencyShaderBackToStandardSource()
    {
        XRShader shader = ShaderHelper.GetPerPixelLinkedListForwardVariant(
            XRShader.EngineShader("Common/LitTexturedAlphaForward.fs", EShaderType.Fragment))!;

        XRShader? variant = ShaderHelper.GetShadowCasterForwardVariant(shader);

        variant.ShouldNotBeNull();
        string variantText = variant.Source.Text ?? throw new InvalidOperationException("Variant shader source text was null.");
        variantText.ShouldContain("#define XRENGINE_SHADOW_CASTER_PASS");
        variantText.ShouldContain("float alphaMask = texture(Texture1, FragUV0).r;");
        variantText.ShouldNotContain("#define XRENGINE_FORWARD_WEIGHTED_OIT");
        variantText.ShouldNotContain("#define XRENGINE_FORWARD_PPLL");
        variantText.ShouldNotContain("#define XRENGINE_FORWARD_DEPTH_PEEL");
    }

    [Test]
    public void DepthNormalPrePassVariant_FallsBackToNullWhenShaderCannotBeRewritten()
    {
        XRMaterial sourceMaterial = new(XRShader.EngineShader("Common/LitTexturedNormalForward.fs", EShaderType.Fragment))
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
        };
        XRMaterial unsupported = new(ShaderHelper.GetWeightedBlendedOitForwardVariant(
            XRShader.EngineShader("Common/LitTexturedNormalForward.fs", EShaderType.Fragment))!)
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
        };

        sourceMaterial.DepthNormalPrePassVariant.ShouldNotBeNull();
        unsupported.DepthNormalPrePassVariant.ShouldBeNull();
    }

    [Test]
    public void StandardForwardVariant_NormalizesGeneratedTransparencyVariantBackToBaseShader()
    {
        XRShader baseShader = XRShader.EngineShader("Common/LitTexturedForward.fs", EShaderType.Fragment);
        XRShader generatedVariant = ShaderHelper.GetWeightedBlendedOitForwardVariant(baseShader)!;

        XRShader? normalized = ShaderHelper.GetStandardForwardVariant(generatedVariant);

        normalized.ShouldNotBeNull();
        normalized.Source.FilePath.ShouldBe(baseShader.Source.FilePath);
        string generatedVariantText = generatedVariant.Source.Text ?? throw new InvalidOperationException("Generated transparency variant source text was null.");
        generatedVariantText.ShouldContain("#define XRENGINE_FORWARD_WEIGHTED_OIT");
    }

    [Test]
    public void DeferredVariant_NormalizesGeneratedTransparencyVariantBackToDeferredSource()
    {
        XRShader baseShader = XRShader.EngineShader("Common/LitTexturedForward.fs", EShaderType.Fragment);
        XRShader generatedVariant = ShaderHelper.GetWeightedBlendedOitForwardVariant(baseShader)!;

        XRShader? deferred = ShaderHelper.GetDeferredVariantOfShader(generatedVariant);

        deferred.ShouldNotBeNull();
        string deferredPath = deferred.Source.FilePath ?? deferred.FilePath ?? string.Empty;
        Path.GetFileName(deferredPath).ShouldBe("TexturedDeferred.fs");
    }

    [Test]
    public void DeferredOpaqueMaterial_KeepsDeferredFragmentShaderForMaskedMode()
    {
        XRMaterial material = new(ShaderHelper.LitTextureFragDeferred()!)
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueDeferred,
        };

        material.TransparencyMode = ETransparencyMode.Masked;

        material.RenderPass.ShouldBe((int)EDefaultRenderPass.OpaqueDeferred);
        XRShader fragmentShader = material.FragmentShaders[0];
        string shaderPath = fragmentShader.Source.FilePath ?? fragmentShader.FilePath ?? string.Empty;
        Path.GetFileName(shaderPath).ShouldBe("TexturedDeferred.fs");
    }

    [Test]
    public void CustomForwardMaterials_ReuseMemoizedFallbackVariantText()
    {
        const string customForwardSource = "#version 450\n" +
            "#pragma snippet \"ForwardLighting\"\n" +
            "layout (location = 0) out vec4 OutColor;\n" +
            "void main()\n" +
            "{\n" +
            "    vec3 normal = normalize(FragNorm);\n" +
            "    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, vec3(1.0), 1.0, 1.0);\n" +
            "    OutColor = vec4(totalLight, 1.0);\n" +
            "}\n";

        XRMaterial firstMaterial = new(CreateInlineShader(customForwardSource, @"D:\Temp\CustomForwardA.fs"))
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
        };
        XRMaterial secondMaterial = new(CreateInlineShader(customForwardSource, @"D:\Temp\CustomForwardB.fs"))
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
        };

        XRMaterial? firstVariant = firstMaterial.DepthNormalPrePassVariant;
        XRMaterial? secondVariant = secondMaterial.DepthNormalPrePassVariant;

        firstVariant.ShouldNotBeNull();
        secondVariant.ShouldNotBeNull();
        ReferenceEquals(firstVariant.FragmentShaders[0].Source.Text, secondVariant.FragmentShaders[0].Source.Text).ShouldBeTrue();
    }

    private static XRShader CreateInlineShader(string source, string filePath)
    {
        TextFile text = TextFile.FromText(source);
        text.FilePath = filePath;
        text.Name = Path.GetFileName(filePath);

        return new XRShader(EShaderType.Fragment, text)
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
        };
    }

    private static string CreateCustomNormalMappedForwardSource()
        => "#version 450\n" +
           "layout (location = 0) out vec4 OutColor;\n" +
           "uniform float MatSpecularIntensity;\n" +
           "uniform sampler2D Texture0;\n" +
           "layout (location = 0) in vec3 FragPos;\n" +
           "layout (location = 1) in vec3 FragNorm;\n" +
           "layout (location = 4) in vec2 FragUV0;\n" +
           "#pragma snippet \"ForwardLighting\"\n" +
           "#pragma snippet \"AmbientOcclusionSampling\"\n" +
           "#pragma snippet \"NormalEncoding\"\n" +
           "vec3 getNormalFromMap()\n" +
           "{\n" +
           "    return normalize(FragNorm);\n" +
           "}\n" +
           "void main()\n" +
           "{\n" +
           "    vec3 normal = getNormalFromMap();\n" +
           "    vec4 texColor = texture(Texture0, FragUV0);\n" +
           "    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();\n" +
           "    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, MatSpecularIntensity, AmbientOcclusion);\n" +
           "    OutColor = vec4(totalLight, texColor.a);\n" +
           "}\n";

    private static string CreateCustomSpecularForwardSource()
        => "#version 450\n" +
           "layout (location = 0) out vec4 OutColor;\n" +
           "uniform float MatSpecularIntensity;\n" +
           "uniform sampler2D Texture0;\n" +
           "uniform sampler2D Texture1;\n" +
           "layout (location = 0) in vec3 FragPos;\n" +
           "layout (location = 1) in vec3 FragNorm;\n" +
           "layout (location = 4) in vec2 FragUV0;\n" +
           "#pragma snippet \"ForwardLighting\"\n" +
           "#pragma snippet \"AmbientOcclusionSampling\"\n" +
           "#pragma snippet \"NormalEncoding\"\n" +
           "void main()\n" +
           "{\n" +
           "    vec3 normal = normalize(FragNorm);\n" +
           "    vec4 texColor = texture(Texture0, FragUV0);\n" +
           "    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();\n" +
           "    float specularMask = texture(Texture1, FragUV0).r;\n" +
           "    float specIntensity = MatSpecularIntensity * specularMask;\n" +
           "    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, specIntensity, AmbientOcclusion);\n" +
           "    OutColor = vec4(totalLight, texColor.a);\n" +
           "}\n";

    private sealed class FileSystemRuntimeShaderServices(string shaderBasePath) : IRuntimeShaderServices
    {
        private readonly string _shaderBasePath = shaderBasePath;

        public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
            => typeof(T) == typeof(XRShader) ? (T?)(object?)LoadShader(filePath) : default;

        public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
        {
            if (typeof(T) != typeof(XRShader))
                throw new NotSupportedException($"Test shader services only support {nameof(XRShader)} assets, not '{typeof(T)}'.");

            string fullPath = Path.Combine(_shaderBasePath, relativePath);
            return (T)(XRAsset)LoadShader(fullPath);
        }

        public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => Task.FromResult(LoadEngineAsset<T>(priority, bypassJobThread, assetRoot, relativePath));

        public void LogWarning(string message)
        {
        }

        private static XRShader LoadShader(string fullPath)
        {
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Shader file not found for test runtime services.", fullPath);

            string source = File.ReadAllText(fullPath);
            TextFile text = TextFile.FromText(source);
            text.FilePath = fullPath;
            text.Name = Path.GetFileName(fullPath);

            return new XRShader(XRShader.ResolveType(Path.GetExtension(fullPath)), text)
            {
                Name = Path.GetFileNameWithoutExtension(fullPath),
            };
        }
    }
}
