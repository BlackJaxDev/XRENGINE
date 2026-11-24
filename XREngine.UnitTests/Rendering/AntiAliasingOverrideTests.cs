using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Shaders;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AntiAliasingOverrideTests
{
    [Test]
    public void ApplyAntiAliasingPreference_DoesNotOverwriteEngineDefaults()
    {
        var originalUserSettings = Engine.UserSettings;
        var originalGameSettings = Engine.GameSettings;
        var originalAntiAliasingMode = RuntimeEngine.Rendering.Settings.AntiAliasingMode;
        var originalAntiAliasingFilterMode = RuntimeEngine.Rendering.Settings.AntiAliasingFilterMode;
        var originalMsaaSampleCount = RuntimeEngine.Rendering.Settings.MsaaSampleCount;

        try
        {
            Engine.UserSettings = new UserSettings();
            Engine.GameSettings = new GameStartupSettings();
            RuntimeEngine.Rendering.Settings.AntiAliasingMode = EAntiAliasingMode.Fxaa;
            RuntimeEngine.Rendering.Settings.AntiAliasingFilterMode = EAntiAliasingFilterMode.Box1x1;
            RuntimeEngine.Rendering.Settings.MsaaSampleCount = 4u;

            Engine.UserSettings.AntiAliasingModeOverride = new OverrideableSetting<EAntiAliasingMode>(EAntiAliasingMode.Taa, true);
            Engine.UserSettings.AntiAliasingFilterModeOverride = new OverrideableSetting<EAntiAliasingFilterMode>(EAntiAliasingFilterMode.Tent2x2, true);
            Engine.UserSettings.MsaaSampleCountOverride = new OverrideableSetting<uint>(8u, true);

            EngineRenderingSettingsApplication.ApplyAntiAliasingPreference();

            RuntimeEngine.Rendering.Settings.AntiAliasingMode.ShouldBe(EAntiAliasingMode.Fxaa);
            RuntimeEngine.Rendering.Settings.AntiAliasingFilterMode.ShouldBe(EAntiAliasingFilterMode.Box1x1);
            RuntimeEngine.Rendering.Settings.MsaaSampleCount.ShouldBe(4u);
            Engine.EffectiveSettings.AntiAliasingMode.ShouldBe(EAntiAliasingMode.Taa);
            Engine.EffectiveSettings.AntiAliasingFilterMode.ShouldBe(EAntiAliasingFilterMode.Tent2x2);
            Engine.EffectiveSettings.MsaaSampleCount.ShouldBe(8u);
        }
        finally
        {
            RuntimeEngine.Rendering.Settings.AntiAliasingMode = originalAntiAliasingMode;
            RuntimeEngine.Rendering.Settings.AntiAliasingFilterMode = originalAntiAliasingFilterMode;
            RuntimeEngine.Rendering.Settings.MsaaSampleCount = originalMsaaSampleCount;
            Engine.UserSettings = originalUserSettings;
            Engine.GameSettings = originalGameSettings;
        }
    }

    [Test]
    public void AntiAliasingFilterMode_ResolvesUserProjectEngineCascade()
    {
        var originalUserSettings = Engine.UserSettings;
        var originalGameSettings = Engine.GameSettings;
        var originalFilterMode = RuntimeEngine.Rendering.Settings.AntiAliasingFilterMode;

        try
        {
            Engine.UserSettings = new UserSettings();
            Engine.GameSettings = new GameStartupSettings();
            RuntimeEngine.Rendering.Settings.AntiAliasingFilterMode = EAntiAliasingFilterMode.Default;

            Engine.EffectiveSettings.AntiAliasingFilterMode.ShouldBe(EAntiAliasingFilterMode.Default);

            Engine.GameSettings.AntiAliasingFilterModeOverride = new OverrideableSetting<EAntiAliasingFilterMode>(EAntiAliasingFilterMode.Box1x1, true);
            Engine.EffectiveSettings.AntiAliasingFilterMode.ShouldBe(EAntiAliasingFilterMode.Box1x1);

            Engine.UserSettings.AntiAliasingFilterModeOverride = new OverrideableSetting<EAntiAliasingFilterMode>(EAntiAliasingFilterMode.Tent2x2, true);
            Engine.EffectiveSettings.AntiAliasingFilterMode.ShouldBe(EAntiAliasingFilterMode.Tent2x2);
        }
        finally
        {
            RuntimeEngine.Rendering.Settings.AntiAliasingFilterMode = originalFilterMode;
            Engine.UserSettings = originalUserSettings;
            Engine.GameSettings = originalGameSettings;
        }
    }

    [TestCase("#define XR_ADV_NATIVE_SHADING_FAMILY 1\n")]
    [TestCase("#define XR_ADV_VISIBILITY_ARRAY 1\n#define XR_ADV_NATIVE_SHADING_FAMILY 1\n")]
    public void ShadeNativeOpaqueMsaa_CompilesToSpirvForVulkan(string variantPreamble)
    {
        string fullPath = ResolveWorkspaceFile("Build/CommonAssets/Shaders/Advanced/Shading/ShadeNativeOpaqueMsaa.comp");
        string shaderSource = File.ReadAllText(fullPath);
        string preamble = variantPreamble + AdvancedShaderAccessLibrary.BuildPreamble(
            RuntimeGraphicsApiKind.Vulkan,
            EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing,
            descriptorSet: 0u,
            resourceDescriptorSet: 2u);
        shaderSource = InsertPreambleAfterVersion(shaderSource, preamble);

        XRShader shader = new(EShaderType.Compute, new TextFile
        {
            FilePath = fullPath,
            Text = shaderSource,
        });

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.Length.ShouldBeGreaterThan(0);
        rewrittenSource.ShouldNotBeNullOrWhiteSpace();
    }

    [TestCase("Build/CommonAssets/Shaders/Scene3D/ResolveColorFromTextureMS.fs", "")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/TemporalAccumulation.fs", "")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/TemporalAccumulationStereo.fs", "")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolution.fs", "")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolution.fs", "#define XR_TSR_STABLE_OUTPUT 1\n#define XR_ADVANCED_REACTIVE_MASK 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolutionStereo.fs", "")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolutionStereo.fs", "#define XR_TSR_STABLE_OUTPUT 1\n#define XR_ADVANCED_REACTIVE_MASK 1\n")]
    public void AntiAliasingFilterFragmentShaders_CompileToSpirvForVulkan(string relativePath, string variantPreamble)
    {
        string fullPath = ResolveWorkspaceFile(relativePath);
        string shaderSource = File.ReadAllText(fullPath);
        if (!string.IsNullOrEmpty(variantPreamble))
            shaderSource = InsertPreambleAfterVersion(shaderSource, variantPreamble);

        XRShader shader = new(EShaderType.Fragment, new TextFile
        {
            FilePath = fullPath,
            Text = shaderSource,
        });

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.Length.ShouldBeGreaterThan(0);
        rewrittenSource.ShouldNotBeNullOrWhiteSpace();
    }

    private static string ResolveWorkspaceFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(directory, relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException($"Could not locate workspace file '{relativePath}'.");
    }

    private static string InsertPreambleAfterVersion(string source, string preamble)
    {
        int versionEnd = source.IndexOf('\n');
        return versionEnd >= 0
            ? source.Insert(versionEnd + 1, preamble + "\n")
            : preamble + "\n" + source;
    }
}
