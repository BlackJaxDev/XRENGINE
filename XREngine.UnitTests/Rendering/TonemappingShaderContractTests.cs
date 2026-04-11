using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class TonemappingShaderContractTests
{
    [Test]
    public void TonemappingSettings_UseMobiusDefaults_AndClampRuntimeValues()
    {
        var tonemapping = new TonemappingSettings();
        tonemapping.Tonemapping.ShouldBe(ETonemappingType.Mobius);
        tonemapping.MobiusTransition.ShouldBe(TonemappingSettings.DefaultMobiusTransition);

        tonemapping.MobiusTransition = -1.0f;
        tonemapping.MobiusTransition.ShouldBe(TonemappingSettings.MinMobiusTransition);

        var colorGrading = new ColorGradingSettings();
        colorGrading.Gamma = 0.0f;
        colorGrading.Gamma.ShouldBe(0.1f);
    }

    [Test]
    public void PostProcessShader_UsesSharedTonemapSnippet_AndAlignedDefaults()
    {
        ((int)ETonemappingType.Neutral).ShouldBe(7);
        ((int)ETonemappingType.Filmic).ShouldBe(8);

        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/PostProcess.fs").Replace("\r\n", "\n");
        shaderSource.ShouldContain("#include \"../Snippets/ToneMapping.glsl\"");
        shaderSource.ShouldContain("uniform int TonemapType = XRENGINE_TONEMAP_MOBIUS;");
        shaderSource.ShouldContain("uniform float MobiusTransition = 0.6f;");
        shaderSource.ShouldContain("sceneColor = XRENGINE_ApplyToneMap(hdrSceneColor, TonemapType, GetExposure(), ColorGrade.Gamma, MobiusTransition);");
        shaderSource.ShouldNotContain("vec3 NeutralTM(vec3 c)");
        shaderSource.ShouldNotContain("vec3 FilmicTM(vec3 c)");
    }

    [Test]
    public void SharedTonemapSources_UseDistinctNeutralAndFilmicCurves_AndMobiusControl()
    {
        string snippetSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Snippets/ToneMapping.glsl").Replace("\r\n", "\n");
        snippetSource.ShouldContain("#define XRENGINE_TONEMAP_NEUTRAL  7");
        snippetSource.ShouldContain("#define XRENGINE_TONEMAP_FILMIC   8");
        snippetSource.ShouldContain("case XRENGINE_TONEMAP_NEUTRAL:  return XRENGINE_NeutralToneMap(hdr, exposure);");
        snippetSource.ShouldContain("case XRENGINE_TONEMAP_FILMIC:   return XRENGINE_FilmicToneMap(hdr, exposure);");
        snippetSource.ShouldContain("return (x * (x + 0.0245786)) / (x * (0.983729 * x + 0.432951) + 0.238081);");
        snippetSource.ShouldContain("vec3 x = max(hdr * exposure - vec3(0.004), vec3(0.0));");
        snippetSource.ShouldContain("return (x * (6.2 * x + 0.5)) / (x * (6.2 * x + 1.7) + 0.06);");
        snippetSource.ShouldContain("vec3 XRENGINE_MobiusToneMap(vec3 hdr, float exposure, float transition)");
        snippetSource.ShouldContain("return XRENGINE_MobiusToneMap(hdr, exposure, mobiusTransition);");

        string standaloneShaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/TonemapStandalone.fs").Replace("\r\n", "\n");
        standaloneShaderSource.ShouldContain("#include \"../Snippets/ToneMapping.glsl\"");
        standaloneShaderSource.ShouldContain("uniform int TonemapType = XRENGINE_TONEMAP_MOBIUS;");
        standaloneShaderSource.ShouldContain("uniform float MobiusTransition = 0.6;");
        standaloneShaderSource.ShouldContain("OutColor = vec4(XRENGINE_ApplyToneMap(color, TonemapType, Exposure, Gamma, MobiusTransition), src.a);");

        string commandSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_Tonemap.cs").Replace("\r\n", "\n");
        commandSource.ShouldContain("public ETonemappingType TonemapType { get; set; } = ETonemappingType.Mobius;");
        commandSource.ShouldContain("public float MobiusTransition { get; set; } = TonemappingSettings.DefaultMobiusTransition;");
        commandSource.ShouldContain("ShaderHelper.LoadEngineShader(\"Scene3D/TonemapStandalone.fs\", EShaderType.Fragment)");
        commandSource.ShouldContain("program.Uniform(\"MobiusTransition\", Math.Clamp(MobiusTransition, TonemappingSettings.MinMobiusTransition, TonemappingSettings.MaxMobiusTransition));");
    }

    [Test]
    public void PipelineTonemappingStage_IsBacked_AndUsesTonemappingSettingsUniforms()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("DescribeTonemappingStage(builder.Stage(TonemappingStageKey, \"Tonemapping\").BackedBy<TonemappingSettings>());");
        pipelineSource.ShouldContain("visibilityCondition: IsMobius");
        pipelineSource.ShouldContain("(tonemapping ?? new TonemappingSettings()).SetUniforms(program);");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.PostProcessing.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("DescribeTonemappingStage(builder.Stage(TonemappingStageKey, \"Tonemapping\").BackedBy<TonemappingSettings>());");
        pipeline2Source.ShouldContain("visibilityCondition: IsMobius");
        pipeline2Source.ShouldContain("(tonemapping ?? new TonemappingSettings()).SetUniforms(program);");
    }

    [Test]
    public void HsvColorGrading_IsAppliedForHdrAndStandalonePaths()
    {
        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/PostProcess.fs").Replace("\r\n", "\n");
        shaderSource.ShouldContain("sceneColor = ApplyHsvColorGrade(sceneColor);");
        shaderSource.ShouldNotContain("if (!OutputHDR && (ColorGrade.Hue != 1.0f || ColorGrade.Saturation != 1.0f || ColorGrade.Brightness != 1.0f))");

        string commandSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_ColorGrading.cs").Replace("\r\n", "\n");
        commandSource.ShouldContain("sceneColor = ApplyHsvColorGrade(sceneColor);");
        commandSource.ShouldNotContain("if (!OutputHDR && (ColorGrade.Hue != 1.0 || ColorGrade.Saturation != 1.0 || ColorGrade.Brightness != 1.0))");
    }

    [Test]
    public void VignetteStage_Defaults_AndShaderBindings_AreExposed()
    {
        var settings = new VignetteSettings();
        settings.Enabled.ShouldBeFalse();
        settings.Intensity.ShouldBe(0.35f);
        settings.Power.ShouldBe(2.0f);

        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/PostProcess.fs").Replace("\r\n", "\n");
        shaderSource.ShouldContain("sceneColor = ApplyVignette(sceneColor, uv);");

        string stereoShaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/PostProcessStereo.fs").Replace("\r\n", "\n");
        stereoShaderSource.ShouldContain("uniform VignetteStruct Vignette;");
        stereoShaderSource.ShouldContain("ldrSceneColor = ApplyVignette(ldrSceneColor, uv);");

        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("DescribeVignetteStage(builder.Stage(VignetteStageKey, \"Vignette\").BackedBy<VignetteSettings>());");
        pipelineSource.ShouldContain(".IncludeStages(TonemappingStageKey, ColorGradingStageKey, VignetteStageKey);");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.PostProcessing.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("DescribeVignetteStage(builder.Stage(VignetteStageKey, \"Vignette\").BackedBy<VignetteSettings>());");
        pipeline2Source.ShouldContain(".IncludeStages(TonemappingStageKey, ColorGradingStageKey, VignetteStageKey);");
    }

    [Test]
    public void StandaloneTonemapShader_CompilesToSpirv_ForVulkan()
    {
        string fullPath = ResolveWorkspacePath("Build/CommonAssets/Shaders/Scene3D/TonemapStandalone.fs");
        var shaderSource = new TextFile
        {
            FilePath = fullPath,
            Text = File.ReadAllText(fullPath)
        };

        XRShader shader = new(EShaderType.Fragment, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
        rewrittenSource.ShouldNotBeNull();
        rewrittenSource.ShouldContain("XRENGINE_MobiusToneMap");
        rewrittenSource.ShouldContain("XRENGINE_FilmicToneMap");
    }

    [Test]
    public void PostProcessShader_CompilesToSpirv_ForVulkan()
    {
        string fullPath = ResolveWorkspacePath("Build/CommonAssets/Shaders/Scene3D/PostProcess.fs");
        var shaderSource = new TextFile
        {
            FilePath = fullPath,
            Text = File.ReadAllText(fullPath)
        };

        XRShader shader = new(EShaderType.Fragment, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
        rewrittenSource.ShouldNotBeNull();
        rewrittenSource.ShouldContain("ApplyHsvColorGrade");
        rewrittenSource.ShouldContain("ApplyVignette");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}