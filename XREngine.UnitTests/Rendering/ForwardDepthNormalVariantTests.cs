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
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousServices;
    }

    [Test]
    public void NormalMappedForwardShader_RewritesToDepthNormalVariant()
    {
        string source = LoadShaderSource("Common/LitTexturedNormalForward.fs");

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
        string source = LoadShaderSource("Common/LitTexturedSpecForward.fs");

        bool success = ForwardDepthNormalVariantFactory.TryCreateFragmentVariantSource(source, out string variantSource);

        success.ShouldBeTrue();
        variantSource.ShouldContain("float specularMask = texture(Texture1, FragUV0).r;");
        variantSource.ShouldContain("float specIntensity = MatSpecularIntensity * specularMask;");
        variantSource.ShouldContain("Normal = XRENGINE_EncodeNormal(normal);");
        variantSource.ShouldNotContain("XRENGINE_SampleAmbientOcclusion");
        variantSource.ShouldNotContain("#pragma snippet \"AmbientOcclusionSampling\"");
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
        variantText.ShouldContain("Normal = XRENGINE_EncodeNormal(normalize(FragNorm));");
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
        XRShader shader = XRShader.EngineShader("Common/LitTexturedAlphaForwardPpll.fs", EShaderType.Fragment);

        XRShader? variant = ShaderHelper.GetShadowCasterForwardVariant(shader);

        variant.ShouldNotBeNull();
        string variantText = variant.Source.Text ?? throw new InvalidOperationException("Variant shader source text was null.");
        variantText.ShouldContain("#define XRENGINE_SHADOW_CASTER_PASS");
        variantText.ShouldContain("float alphaMask = texture(Texture1, FragUV0).r;");
        variantText.ShouldNotContain("XRE_WriteWeightedBlendedOit");
        variantText.ShouldNotContain("XRE_StorePerPixelLinkedListFragment");
    }

    [Test]
    public void DepthNormalPrePassVariant_FallsBackToNullWhenShaderCannotBeRewritten()
    {
        XRMaterial sourceMaterial = new(XRShader.EngineShader("Common/LitTexturedNormalForward.fs", EShaderType.Fragment))
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
        };
        XRMaterial unsupported = new(XRShader.EngineShader("Common/LitTexturedNormalForwardWeightedOit.fs", EShaderType.Fragment))
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
        };

        sourceMaterial.DepthNormalPrePassVariant.ShouldNotBeNull();
        unsupported.DepthNormalPrePassVariant.ShouldBeNull();
    }

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
