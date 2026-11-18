using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using XREngine.ShaderTranslation.Compilation;
using XREngine.ShaderTranslation.Diagnostics;
using XREngine.ShaderTranslation.Translation;

namespace XREngine.UnitTests.Rendering.ShaderTranslation;

/// <summary>
/// Runs the full Slang and SPIRV-Cross path on the synthetic shaders when both tools are installed.
/// </summary>
[TestFixture]
public sealed class ShaderTranslatorToolTests
{
    [TestCase("TranslationUnlit.shader", 1)]
    [TestCase("TranslationToonLit.shader", 3)]
    [TestCase("TranslationScreenEye.shader", 1)]
    public async Task TranslateFile_SyntheticShader_ProducesEngineGlsl(string fileName, int expectedPasses)
    {
        SlangSpirvCompiler? compiler = SlangSpirvCompiler.FindDefault();
        SpirvCrossGlslEmitter? crossCompiler = SpirvCrossGlslEmitter.FindDefault();
        if (compiler is null || crossCompiler is null)
            Assert.Ignore("slangc and spirv-cross are not installed (set XRE_SLANGC and XRE_SPIRV_CROSS, or install the Vulkan SDK).");

        string work = Path.Combine(Path.GetTempPath(), "xre-shader-translation-tests");
        ShaderTranslator translator = new(compiler!, crossCompiler!);
        ShaderTranslationResult result = await translator.TranslateFileAsync(
            UnityShaderTestData.PathOf(fileName),
            new ShaderTranslationRequest
            {
                MaterialKeywords = new System.Collections.Generic.HashSet<string> { "_NORMALMAP" },
                IncludeDirectories = [UnityShaderTestData.CompatDirectory],
                WorkDirectory = work,
            });

        result.Diagnostics.Where(diagnostic => diagnostic.Severity == ShaderTranslationSeverity.Error).ShouldBeEmpty();
        result.Passes.Count.ShouldBe(expectedPasses);
        foreach (TranslatedStage stage in result.Passes.SelectMany(pass => pass.Stages))
        {
            stage.Glsl.ShouldStartWith("#version 460");
            stage.Glsl.ShouldNotContain("SPIRV_Cross_Combined");
            stage.Glsl.ShouldNotContain("globalParams.");
        }

        TranslatedStage vertex = result.Passes[0].Stages.First(stage => stage.Stage == ShaderProgramStage.Vertex);
        vertex.Glsl.ShouldContain("uniform mat4 ModelMatrix;");
        vertex.Glsl.ShouldContain("layout(location = 0) in ");
    }
}
