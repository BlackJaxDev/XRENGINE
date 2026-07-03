using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class ColorGradingSettingsTests
{
    [Test]
    public void Defaults_MatchPipelineSchemaDefaults()
    {
        var settings = new ColorGradingSettings();

        settings.AutoExposureMetering.ShouldBe(ColorGradingSettings.AutoExposureMeteringMode.LogAverage);
        settings.ExposureDividend.ShouldBe(0.1f);
        settings.MinExposure.ShouldBe(0.0001f);
        settings.MaxExposure.ShouldBe(100.0f);
    }

    [Test]
    public void GetResolvedExposureBounds_OrdersInvertedBounds()
    {
        var settings = new ColorGradingSettings
        {
            MinExposure = 10.0f,
            MaxExposure = 0.0001f,
        };

        settings.GetResolvedExposureBounds(out float minExposure, out float maxExposure);

        minExposure.ShouldBe(0.0001f);
        maxExposure.ShouldBe(10.0f);
    }

    [Test]
    public void ClampExposureToResolvedBounds_UsesOrderedBounds()
    {
        var settings = new ColorGradingSettings
        {
            MinExposure = 10.0f,
            MaxExposure = 0.0001f,
        };

        settings.ClampExposureToResolvedBounds(-1.0f).ShouldBe(0.0001f);
        settings.ClampExposureToResolvedBounds(5.0f).ShouldBe(5.0f);
        settings.ClampExposureToResolvedBounds(20.0f).ShouldBe(10.0f);
    }

    [Test]
    public void SanitizeGpuAutoExposureDeltaSeconds_ClampsSkippedRenderOnDemandGaps()
    {
        ColorGradingSettings.SanitizeGpuAutoExposureDeltaSeconds(double.NaN).ShouldBe(0.0f);
        ColorGradingSettings.SanitizeGpuAutoExposureDeltaSeconds(-1.0).ShouldBe(0.0f);
        ColorGradingSettings.SanitizeGpuAutoExposureDeltaSeconds(1.0 / 120.0).ShouldBe((float)(1.0 / 120.0));
        ColorGradingSettings.SanitizeGpuAutoExposureDeltaSeconds(1.0).ShouldBe(ColorGradingSettings.MaxGpuAutoExposureDeltaSeconds);
    }

    [Test]
    public void ColorGradingSettings_DoesNotUseFrameCountExposureHolds()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Camera/ColorGradingSettings.cs")
            .Replace("\r\n", "\n");

        source.ShouldContain("SanitizeGpuAutoExposureDeltaSeconds(rawRenderDeltaSeconds)");
        source.ShouldNotContain("_autoExposureHoldUntilRenderFrameId");
        source.ShouldNotContain("SuppressAutoExposureUpdatesThisFrame");
        source.ShouldNotContain("HoldAutoExposureUpdatesForRenderFrames");
        source.ShouldNotContain("ShouldHoldGpuAutoExposureAfterRenderGap");
    }

    [Test]
    public void ExposureUpdate_HoldsGpuAutoExposureDuringSuppressedViewportFrames()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ExposureUpdate.cs")
            .Replace("\r\n", "\n");

        string suppressionGuard = "XRViewport? viewport = ActivePipelineInstance.RenderState.WindowViewport ?? ActivePipelineInstance.LastWindowViewport;";
        source.ShouldContain(suppressionGuard);
        source.ShouldContain("if (viewport?.Suppress3DSceneRendering == true)");
        source.ShouldContain("if (viewport?.SuppressAutoExposureUpdates == true)");
        source.ShouldContain("[ExposureUpdate] Holding auto exposure during suppressed 3D viewport frame.");
        source.ShouldContain("[ExposureUpdate] Holding auto exposure while the viewport requested exposure stability.");
        source.ShouldNotContain("CameraSuppressAutoExposureUpdates");
        source.ShouldNotContain("camera requested exposure stability");

        int suppressionIndex = source.IndexOf(suppressionGuard, StringComparison.Ordinal);
        int resetIndex = source.IndexOf("grading.MarkGpuAutoExposureReady(false);", StringComparison.Ordinal);
        suppressionIndex.ShouldBeGreaterThanOrEqualTo(0);
        resetIndex.ShouldBeGreaterThanOrEqualTo(0);
        suppressionIndex.ShouldBeLessThan(resetIndex);
    }

    [Test]
    public void GpuAutoExposureShaders_LerpFromFallbackWhenHistoryTextureWasReset()
    {
        string vulkan = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanRenderer.AutoExposure.cs")
            .Replace("\r\n", "\n");
        string openGl = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Features/Luminance/OpenGLRenderer.LuminanceResources.cs")
            .Replace("\r\n", "\n");

        foreach (string source in new[] { vulkan, openGl })
        {
            source.ShouldContain("uniform float FallbackExposure;");
            source.ShouldContain("float stableCurrent = currentValid ? current : clamp(FallbackExposure, MinExposure, MaxExposure);");
            source.ShouldContain("float outExposure = mix(stableCurrent, target, clamp(ExposureTransitionSpeed, 0.0, 1.0));");
            source.ShouldNotContain("? target\n");
        }

        vulkan.ShouldContain("program.Uniform(\"FallbackExposure\", Math.Clamp(fallbackExposure, minExposure, maxExposure));");
    }

    [Test]
    public void VulkanGpuAutoExposure_UsesBlockMeteringWhenMipPyramidIsUnavailable()
    {
        string vulkan = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanRenderer.AutoExposure.cs")
            .Replace("\r\n", "\n");

        vulkan.ShouldContain("program.Uniform(\"MeteringTargetSize\", settings.AutoExposureMeteringTargetSize);");
        vulkan.ShouldContain("program.Uniform(\"UseMiplessMeteringFallback\", useMiplessMeteringFallback ? 1 : 0);");
        vulkan.ShouldContain("const int BLOCK_TAPS_PER_AXIS = 4;");
        vulkan.ShouldContain("uniform int UseMiplessMeteringFallback;");
        vulkan.ShouldContain("void ComputeAspectGrid(int w, int h, out int gridX, out int gridY)");
        vulkan.ShouldContain("float ReduceWeightedAverage(int tid)");
        vulkan.ShouldContain("if (UseMiplessMeteringFallback != 0 && MeteringMode == 1)");
        vulkan.ShouldContain("float logFloor = max(meanLum * 0.25, 1e-4);");
        vulkan.ShouldNotContain("int y = min(x0 + 0, h - 1);");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string current = Path.GetFullPath(AppContext.BaseDirectory);
        while (!string.IsNullOrEmpty(current))
        {
            string candidate = Path.Combine(current, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null)
                break;

            current = parent.FullName;
        }

        throw new FileNotFoundException($"Could not locate '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }
}
