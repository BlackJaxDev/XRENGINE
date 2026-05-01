using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ForwardAmbientOcclusionShaderTests : GpuTestBase
{
    private IRuntimeShaderServices? _previousServices;

    private static readonly string[] LitForwardShaderPaths =
    [
        "Common/LitColoredForward.fs",
        "Common/LitTexturedForward.fs",
        "Common/LitTexturedAlphaForward.fs",
        "Common/LitTexturedNormalForward.fs",
        "Common/LitTexturedNormalAlphaForward.fs",
        "Common/LitTexturedNormalSpecForward.fs",
        "Common/LitTexturedNormalSpecAlphaForward.fs",
        "Common/LitTexturedSilhouettePOMForward.fs",
        "Common/LitTexturedSpecForward.fs",
        "Common/LitTexturedSpecAlphaForward.fs",
    ];

    private static readonly string[] ConsolidatedForwardShaderPaths =
    [
        "Common/LitTexturedForward.fs",
        "Common/LitTexturedAlphaForward.fs",
        "Common/LitTexturedNormalForward.fs",
        "Common/LitTexturedNormalAlphaForward.fs",
        "Common/LitTexturedNormalSpecForward.fs",
        "Common/LitTexturedNormalSpecAlphaForward.fs",
        "Common/LitTexturedSilhouettePOMForward.fs",
        "Common/LitTexturedSpecForward.fs",
        "Common/LitTexturedSpecAlphaForward.fs",
        "Common/UnlitTexturedForward.fs",
        "Common/UnlitTexturedStereoForward.fs",
        "Common/UnlitAlphaTexturedForward.fs",
    ];

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new FileSystemRuntimeShaderServices(ShaderBasePath);
        ShaderHelper.ClearDefinedVariantSourceCache();
    }

    [TearDown]
    public void TearDown()
    {
        ShaderHelper.ClearDefinedVariantSourceCache();
        RuntimeShaderServices.Current = _previousServices;
    }

    [Test]
    public void AmbientOcclusionSamplingSnippet_DeclaresSharedForwardAoContract()
    {
        string source = LoadShaderSource("Snippets/AmbientOcclusionSampling.glsl");

        source.ShouldContain("uniform sampler2D AmbientOcclusionTexture;");
        source.ShouldContain("uniform sampler2DArray AmbientOcclusionTextureArray;");
        source.ShouldContain("uniform bool AmbientOcclusionArrayEnabled;");
        source.ShouldContain("XRENGINE_GetForwardViewIndex()");
        source.ShouldContain("uniform bool AmbientOcclusionEnabled;");
        source.ShouldContain("float XRENGINE_SampleAmbientOcclusion()");
        source.ShouldContain("vec2 fragCoordLocal = gl_FragCoord.xy - ScreenOrigin;");
        source.ShouldContain("vec2 aoUv = clamp(fragCoordLocal / viewportSize, vec2(0.0), vec2(0.999999));");
        source.ShouldNotContain("ivec2 pixel = ivec2(floor(gl_FragCoord.xy - ScreenOrigin));");
    }

    [Test]
    public void LitForwardShaders_UseSharedAmbientOcclusionSnippet()
    {
        foreach (string path in LitForwardShaderPaths)
        {
            string source = LoadShaderSource(path);

            if (!path.Contains("ColoredForward", System.StringComparison.Ordinal))
            {
                source.ShouldContain("void XRENGINE_BeginForwardFragmentOutput()");
                source.ShouldContain("void XRENGINE_WriteForwardFragment(vec4 shadedColor)");
            }
            source.ShouldContain("#pragma snippet \"AmbientOcclusionSampling\"");
            source.ShouldContain("XRENGINE_SampleAmbientOcclusion()");
            source.ShouldNotContain("float AmbientOcclusion = 1.0;");
            source.ShouldNotContain("MatSpecularIntensity, 1.0)");
        }
    }

    [Test]
    public void ConsolidatedTexturedForwardShader_DeclaresSharedTransparentOutputHelpers()
    {
        string source = LoadShaderSource("Common/LitTexturedForward.fs");

        source.ShouldContain("#if defined(XRENGINE_FORWARD_WEIGHTED_OIT)");
        source.ShouldContain("#if defined(XRENGINE_FORWARD_PPLL)");
        source.ShouldContain("#elif defined(XRENGINE_FORWARD_DEPTH_PEEL)");
        source.ShouldContain("void XRENGINE_BeginForwardFragmentOutput()");
        source.ShouldContain("void XRENGINE_WriteForwardFragment(vec4 shadedColor)");
    }

    [Test]
    public void ConsolidatedForwardShaders_GuardSharedColorHelpers_ForDepthAndShadowPasses()
    {
        foreach (string path in ConsolidatedForwardShaderPaths)
        {
            string source = LoadShaderSource(path);

            source.ShouldContain("void XRENGINE_WriteForwardFragment(vec4 shadedColor)");
            source.ShouldContain("#elif defined(XRENGINE_DEPTH_NORMAL_PREPASS) || defined(XRENGINE_SHADOW_CASTER_PASS)");
            source.ShouldContain("    return;");
        }
    }

    [Test]
    public void GeneratedTransparentForwardVariants_ReuseLitAmbientOcclusionSource()
    {
        foreach (string path in LitForwardShaderPaths)
        {
            XRShader shader = XRShader.EngineShader(path, EShaderType.Fragment);

            foreach (XRShader? variant in new[]
            {
                ShaderHelper.GetWeightedBlendedOitForwardVariant(shader),
                ShaderHelper.GetPerPixelLinkedListForwardVariant(shader),
                ShaderHelper.GetDepthPeelingForwardVariant(shader),
            })
            {
                variant.ShouldNotBeNull();

                string source = variant.Source.Text ?? string.Empty;
                source.ShouldContain("#pragma snippet \"AmbientOcclusionSampling\"");
                source.ShouldContain("XRENGINE_SampleAmbientOcclusion()");
            }
        }
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

            XRShader shader = new(XRShader.ResolveType(Path.GetExtension(fullPath)), text)
            {
                Name = Path.GetFileNameWithoutExtension(fullPath),
            };

            return shader;
        }
    }
}
