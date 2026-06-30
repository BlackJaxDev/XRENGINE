using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class OpenXrStereoTemporalIsolationCompletionTests
{
    [Test]
    public void AutoExposure_UsesExplicitHeadsetSharedVrPolicy()
    {
        string contracts = ReadWorkspaceFile("XREngine.Runtime.Core/Settings/VrRenderingContracts.cs");
        string exposure = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ExposureUpdate.cs");
        string vulkanExposure = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanRenderer.AutoExposure.cs");

        contracts.ShouldContain("public enum EVrAutoExposurePolicy");
        contracts.ShouldContain("HeadsetShared");
        contracts.ShouldContain("PerEye");
        contracts.ShouldContain("LeftEyeOnly");

        exposure.ShouldContain("TryUseAutoExposureSource");
        exposure.ShouldContain("EVrAutoExposurePolicy.HeadsetShared");
        exposure.ShouldContain("sourceTexture is XRTexture2DArray stereoArray && stereoArray.Depth >= 2u");
        exposure.ShouldContain("external per-eye swapchain targets are skipped to avoid last-eye-wins exposure state");
        exposure.ShouldContain("ReportVrAutoExposurePolicy");
        exposure.ShouldContain("ReportSkippedVrAutoExposure");

        vulkanExposure.ShouldContain("uniform sampler2DArray SourceTex;");
        vulkanExposure.ShouldContain("program.Uniform(\"LayerCount\", layerCount);");
        vulkanExposure.ShouldContain("lumDot /= float(layers);");
        vulkanExposure.ShouldContain("Auto exposure policy=HeadsetShared");
    }

    [Test]
    public void AtmosphereAndFog_DisableTemporalHistoryInVrUntilStereoArraysExist()
    {
        string atmosphere = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_AtmosphereHistoryPass.cs");
        string fog = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VolumetricFogHistoryPass.cs");
        string commandChain = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs");

        atmosphere.ShouldContain("TryUseTemporalAtmosphereHistory");
        atmosphere.ShouldContain("VPRC_TemporalAccumulationPass.ResolveHistoryIsolationPolicy");
        atmosphere.ShouldContain("VR atmosphere temporal history disabled until half-resolution atmosphere textures and shaders are stereo array-layered");
        atmosphere.ShouldContain("ResetState(state);");
        atmosphere.ShouldContain("HistoryReady = TryUseTemporalAtmosphereHistory(out _, out _)");

        fog.ShouldContain("TryUseTemporalVolumetricFogHistory");
        fog.ShouldContain("VPRC_TemporalAccumulationPass.ResolveHistoryIsolationPolicy");
        fog.ShouldContain("VR volumetric fog temporal history disabled until half-resolution fog textures and shaders are stereo array-layered");
        fog.ShouldContain("ResetState(state);");
        fog.ShouldContain("HistoryReady = TryUseTemporalVolumetricFogHistory(out _, out _)");

        commandChain.ShouldContain("if (!Stereo)");
        commandChain.ShouldContain("AppendAtmosphericScattering(c);");
        commandChain.ShouldContain("AppendVolumetricFog(c);");
    }

    [Test]
    public void VendorUpscale_FailsLoudlyForUnsupportedVrVendorPaths()
    {
        string vendor = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs");

        vendor.ShouldContain("VrVendorUpscaleSupportMatrix");
        vendor.ShouldContain("native NVIDIA DLSS upscale=unsupported");
        vendor.ShouldContain("DLAA=unsupported");
        vendor.ShouldContain("DLSS frame generation=unsupported");
        vendor.ShouldContain("Intel XeSS=unsupported");
        vendor.ShouldContain("Intel XeSS frame generation=unsupported");
        vendor.ShouldContain("OpenGL-to-Vulkan bridge=unsupported");
        vendor.ShouldContain("fallback blit=supported");
        vendor.ShouldContain("TryValidateVrVendorUpscaleSupport");
        vendor.ShouldContain("FailRequestedVendorFeature(\"VR vendor upscale/frame generation\", vrVendorFailure);");
        vendor.ShouldContain("ResetVendorHistoryForUnsupportedVr");
        vendor.ShouldContain("_nativeDlssDispatchHistoryValid = false;");
        vendor.ShouldContain("_bridgeVendorHistoryValid = false;");
        vendor.ShouldContain("_bridgeDispatchHistoryValid = false;");
    }

    [Test]
    public void ParallelRecordingAndModeSwitching_SurfaceBottlenecksAndResourceKeys()
    {
        string vulkanOpenXr = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string pipelineInstance = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        string resourceKey = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Resources/Records/ResourceGenerationKey.cs");

        vulkanOpenXr.ShouldContain("Monitor.Enter(_oneTimeSubmitLock, ref queueLockTaken);");
        vulkanOpenXr.ShouldContain("LogOpenXrSerializedCriticalSectionWait(\"QueueSubmit\"");
        vulkanOpenXr.ShouldContain("serialized critical section={0} waitMs={1:F3}");

        pipelineInstance.ShouldContain("externalSwapchainTarget");
        pipelineInstance.ShouldContain("reservedViewCount = stereo && !externalSwapchainTarget ? 2u : 1u;");
        pipelineInstance.ShouldContain("reservedEyeIndex");
        pipelineInstance.ShouldContain("ExternalSwapchainFrameProfileChanged");

        resourceKey.ShouldContain("ReservedViewCount");
        resourceKey.ShouldContain("ReservedEyeIndex");
        resourceKey.ShouldContain("views={ReservedViewCount} eye={ReservedEyeIndex}");
    }

    [Test]
    public void DocsAndProfileRunner_RecordFinalOpenXrStereoPolicies()
    {
        string openXrDoc = ReadWorkspaceFile("docs/architecture/rendering/openxr-vr-rendering.md");
        string openVrDoc = ReadWorkspaceFile("docs/architecture/rendering/openvr-rendering.md");
        string pipelineDoc = ReadWorkspaceFile("docs/architecture/rendering/default-render-pipeline-notes.md");
        string profileRunner = ReadWorkspaceFile("Tools/OpenXR/Run-OpenXrModeProfileMatrix.ps1");

        openXrDoc.ShouldContain("OpenXR Vulkan Stereo Mode Matrix");
        openXrDoc.ShouldContain("EVrAutoExposurePolicy.HeadsetShared");
        openXrDoc.ShouldContain("Vendor upscalers are intentionally unsupported for headset stereo today");
        openXrDoc.ShouldContain("Run-OpenXrModeProfileMatrix.ps1");
        openXrDoc.ShouldContain("DisabledExternalPerEyeSwapchain");

        openVrDoc.ShouldContain("OpenVR `SinglePassStereo` is the engine-owned stereo-array");
        openVrDoc.ShouldContain("OpenXrSinglePassCompatibility");

        pipelineDoc.ShouldContain("OpenXR Stereo Temporal Isolation");
        pipelineDoc.ShouldContain("Atmosphere and volumetric-fog temporal history stay mono-only");
        pipelineDoc.ShouldContain("Explicit DLSS/DLAA/XeSS", Case.Sensitive);
        pipelineDoc.ShouldContain("must fail loudly");

        profileRunner.ShouldContain("openxr-mode-profile-matrix.csv");
        profileRunner.ShouldContain("openxr-mode-profile-matrix.json");
        profileRunner.ShouldContain("XRE_PROFILE_CAPTURE");
        profileRunner.ShouldContain("XRE_UNIT_TEST_VR_VIEW_RENDER_MODE");
        profileRunner.ShouldContain("XRE_OPENXR_RENDER_PACING_MODE");
        profileRunner.ShouldContain("XRE_OPENXR_VULKAN_SERIAL_EYE_SUBMIT");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        string platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);

        while (dir is not null)
        {
            string fullPath = Path.Combine(dir.FullName, platformPath);
            if (File.Exists(fullPath))
                return File.ReadAllText(fullPath).Replace("\r\n", "\n");

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}
