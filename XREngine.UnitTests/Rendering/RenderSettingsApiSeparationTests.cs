using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;
using XREngine.Rendering.Vulkan;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderSettingsApiSeparationTests
{
    [Test]
    public void EngineSettings_OpenGLLegacyAliasesForwardToGroupedSettings()
    {
        var settings = new Engine.Rendering.EngineSettings();

        settings.AllowShaderPipelines = false;
        settings.OpenGL.AllowProgramPipelines.ShouldBeFalse();

        settings.OpenGL.AllowProgramPipelines = true;
        settings.AllowShaderPipelines.ShouldBeTrue();

        settings.OpenGLShaderLinkStrategy = EOpenGLShaderLinkStrategy.DriverParallel;
        settings.OpenGL.ShaderLinking.Strategy.ShouldBe(EOpenGLShaderLinkStrategy.DriverParallel);

        settings.OpenGL.ShaderLinking.DriverCompilerThreadCount = -1;
        settings.OpenGLShaderCompilerThreadCount.ShouldBe(-1);

        settings.UseDetailPreservingComputeMipmaps = false;
        settings.OpenGL.TextureUpload.UseDetailPreservingComputeMipmaps.ShouldBeFalse();
    }

    [Test]
    public void EngineSettings_VulkanLegacyAliasesForwardToGroupedSettings()
    {
        var settings = new Engine.Rendering.EngineSettings();

        settings.VulkanGpuDrivenProfile = EVulkanGpuDrivenProfile.ShippingFast;
        settings.Vulkan.GpuDriven.Profile.ShouldBe(EVulkanGpuDrivenProfile.ShippingFast);

        settings.Vulkan.GpuDriven.Profile = EVulkanGpuDrivenProfile.DevParity;
        settings.VulkanGpuDrivenProfile.ShouldBe(EVulkanGpuDrivenProfile.DevParity);

        settings.VulkanQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsCompute;
        settings.Vulkan.Synchronization.QueueOverlapMode.ShouldBe(EVulkanQueueOverlapMode.GraphicsCompute);

        settings.EnableVulkanDescriptorIndexing = false;
        settings.Vulkan.Descriptors.EnableDescriptorIndexing.ShouldBeFalse();

        settings.VulkanBindlessMaterialMode = EVulkanBindlessMaterialMode.Required;
        settings.Vulkan.Descriptors.BindlessMaterialMode.ShouldBe(EVulkanBindlessMaterialMode.Required);

        settings.VulkanRenderTargetMode = EVulkanRenderTargetMode.DynamicRendering;
        settings.Vulkan.TargetMode.RenderTargetMode.ShouldBe(EVulkanRenderTargetMode.DynamicRendering);
    }

    [Test]
    public void UserAndGameRenderingOverridesKeepCompatibilityAliases()
    {
        var userSettings = new UserSettings();
        userSettings.RenderLibrary = ERenderLibrary.Vulkan;
        userSettings.PreferredRenderBackend.ShouldBe(ERenderLibrary.Vulkan);

        userSettings.Rendering.Common.RenderBackendFallbackPolicyOverride =
            new OverrideableSetting<RenderBackendFallbackPolicy>(RenderBackendFallbackPolicy.FallbackWithWarning, true);
        userSettings.RenderBackendFallbackPolicyOverride.Value.ShouldBe(RenderBackendFallbackPolicy.FallbackWithWarning);
        userSettings.RenderBackendFallbackPolicyOverride.HasOverride.ShouldBeTrue();

        var startupSettings = new GameStartupSettings();
        startupSettings.RenderBackendFallbackPolicyOverride =
            new OverrideableSetting<RenderBackendFallbackPolicy>(RenderBackendFallbackPolicy.AutoPreferRequested, true);
        startupSettings.Rendering.Common.RenderBackendFallbackPolicyOverride.Value.ShouldBe(RenderBackendFallbackPolicy.AutoPreferRequested);

        startupSettings.Rendering.Vulkan.RenderTargetModeOverride =
            new OverrideableSetting<EVulkanRenderTargetMode>(EVulkanRenderTargetMode.LegacyRenderPass, true);
        startupSettings.VulkanRenderTargetModeOverride.Value.ShouldBe(EVulkanRenderTargetMode.LegacyRenderPass);
        startupSettings.VulkanRenderTargetModeOverride.HasOverride.ShouldBeTrue();
    }

    [Test]
    public void UnitTestingWorldSettings_GroupedRenderingOverridesLegacyRenderApi()
    {
        const string json = """
        {
          "RenderAPI": "OpenGL",
          "Rendering": {
            "RenderBackend": "Vulkan",
            "BackendFallbackPolicy": "FallbackWithWarning",
            "OpenGL": {
              "AllowProgramPipelines": false,
              "ShaderLinking": {
                "Strategy": "DriverParallel",
                "DriverCompilerThreadCount": 0
              }
            },
            "Vulkan": {
              "RenderTargetMode": "LegacyRenderPass"
            }
          }
        }
        """;

        UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(json);
        settings.Rendering.RenderBackend.ShouldBe(ERenderLibrary.Vulkan);
        settings.Rendering.BackendFallbackPolicy.ShouldBe(RenderBackendFallbackPolicy.FallbackWithWarning);
        settings.Rendering.OpenGL.AllowProgramPipelines.ShouldBeFalse();
        settings.Rendering.OpenGL.ShaderLinking.Strategy.ShouldBe(EOpenGLShaderLinkStrategy.DriverParallel);
        settings.Rendering.Vulkan.RenderTargetMode.ShouldBe(EVulkanRenderTargetMode.LegacyRenderPass);

        var userSettings = new UserSettings();
        UnitTestingWorldSettingsStore.ApplyUserSettingsOverrides(userSettings, settings).ShouldBeTrue();
        userSettings.PreferredRenderBackend.ShouldBe(ERenderLibrary.Vulkan);
        userSettings.RenderBackendFallbackPolicyOverride.Value.ShouldBe(RenderBackendFallbackPolicy.FallbackWithWarning);
        userSettings.RenderBackendFallbackPolicyOverride.HasOverride.ShouldBeTrue();

        var startupSettings = new GameStartupSettings();
        UnitTestingWorldSettingsStore.ApplyStartupOverrides(startupSettings, settings);
        startupSettings.DefaultUserSettings.PreferredRenderBackend.ShouldBe(ERenderLibrary.Vulkan);
        startupSettings.RenderBackendFallbackPolicyOverride.Value.ShouldBe(RenderBackendFallbackPolicy.FallbackWithWarning);
        startupSettings.VulkanRenderTargetModeOverride.Value.ShouldBe(EVulkanRenderTargetMode.LegacyRenderPass);
    }

    [Test]
    public void EditorPreferenceGroupedAdaptersForwardToSerializedFlatSettings()
    {
        var preferences = new EditorPreferences();

        preferences.Viewport.PresentationMode = EditorPreferences.EViewportPresentationMode.UseViewportPanel;
        preferences.ViewportPresentationMode.ShouldBe(EditorPreferences.EViewportPresentationMode.UseViewportPanel);

        preferences.Viewport.SceneDepthMode = EditorPreferences.ESceneDepthModePreference.Reversed;
        preferences.SceneDepthMode.ShouldBe(EditorPreferences.ESceneDepthModePreference.Reversed);

        preferences.Selection.HoverOutlineEnabled = false;
        preferences.HoverOutlineEnabled.ShouldBeFalse();

        preferences.Diagnostics.Vulkan.TraceAllDraws = true;
        preferences.Debug.VkTraceDraw.ShouldBeTrue();

        preferences.Diagnostics.OpenGL.SubmitTraceLevel = 2;
        preferences.Debug.GLSubmitTraceLevel.ShouldBe(2);
    }

    [Test]
    public void EditorPreferenceGroupedOverridesApplyThroughCompatibilityAdapters()
    {
        var preferences = new EditorPreferences();
        var overrides = new EditorPreferencesOverrides();

        overrides.Viewport.PresentationModeOverride =
            new OverrideableSetting<EditorPreferences.EViewportPresentationMode>(
                EditorPreferences.EViewportPresentationMode.UseViewportPanel,
                true);
        overrides.Selection.HoverOutlineEnabledOverride = new OverrideableSetting<bool>(false, true);
        overrides.Diagnostics.Culling.ZeroReadbackMaterialDrawPathOverride =
            new OverrideableSetting<EZeroReadbackMaterialDrawPath>(EZeroReadbackMaterialDrawPath.ActiveBucketList, true);
        overrides.Diagnostics.Profiler.CpuTimingDisplayModeOverride =
            new OverrideableSetting<ProfilerTimingDisplayMode>(ProfilerTimingDisplayMode.Average, true);

        preferences.ApplyOverrides(overrides);

        preferences.ViewportPresentationMode.ShouldBe(EditorPreferences.EViewportPresentationMode.UseViewportPanel);
        preferences.HoverOutlineEnabled.ShouldBeFalse();
        preferences.Debug.ZeroReadbackMaterialDrawPath.ShouldBe(EZeroReadbackMaterialDrawPath.ActiveBucketList);
        preferences.Debug.ProfilerPanelCpuTimingDisplayMode.ShouldBe(ProfilerTimingDisplayMode.Average);
    }

    [Test]
    public void RenderBackendSelectionAndVulkanTargetModeUseSeparatedPolicySources()
    {
        string effective = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Engine.EffectiveSettings.cs");
        string windows = ReadWorkspaceFile("XRENGINE/Engine/Engine.Windows.cs");
        string mode = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderTargetMode.cs");
        string runtimeServices = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderingHostServices.cs");

        effective.ShouldContain("public static ERenderLibrary PreferredRenderBackend");
        effective.ShouldContain("public static RenderBackendFallbackPolicy RenderBackendFallbackPolicy");
        effective.ShouldContain("Rendering.Settings.Vulkan.Startup.FallbackPolicy");
        effective.ShouldContain("GameSettings?.RenderBackendFallbackPolicyOverride");
        effective.ShouldContain("UserSettings?.RenderBackendFallbackPolicyOverride");
        effective.ShouldContain("public static EVulkanRenderTargetMode VulkanRenderTargetMode");
        effective.ShouldContain("GameSettings?.VulkanRenderTargetModeOverride");
        effective.ShouldContain("public static EffectiveRenderSettingsSnapshot RenderSnapshot");

        windows.ShouldContain("EffectiveSettings.PreferredRenderBackend");
        windows.ShouldContain("RenderBackendFallbackPolicy.RequireRequested");
        windows.ShouldContain("AllowsRenderBackendFallback");
        windows.ShouldContain("Vulkan initialization failed and render backend fallback is not permitted.");
        windows.ShouldContain("[StartupWindow] Ignoring render backend fallback policy");

        mode.ShouldContain("XRE_VK_RENDER_TARGET_MODE");
        mode.ShouldContain("RuntimeEngine.EffectiveSettings.VulkanRenderTargetMode");
        mode.ShouldContain("dynamic rendering was explicitly requested");

        runtimeServices.ShouldContain("EVulkanRenderTargetMode VulkanRenderTargetMode");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
            {
                string path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
                return File.ReadAllText(path);
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing XRENGINE.slnx.");
    }
}
