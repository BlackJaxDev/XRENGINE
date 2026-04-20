using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanUpscaleBridgeTodoCompletionTests
{
    [Test]
    public void UnavailabilityDescription_ReportsMissingInteropAndProbeFailure()
    {
        WithBridgeSnapshot(
            new global::XREngine.VulkanUpscaleBridgeCapabilitySnapshot
            {
                EnvironmentEnabled = false,
                WindowsOnly = false,
                MonoViewportOnly = false,
                HdrSupported = true,
                HasOpenGlExternalMemory = false,
                HasOpenGlExternalMemoryWin32 = false,
                HasOpenGlSemaphore = false,
                HasOpenGlSemaphoreWin32 = false,
                VulkanProbeSucceeded = false,
                ProbeFailureReason = "probe failed during test",
            },
            () =>
            {
                string reason = global::XREngine.Engine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(null, hdrRequested: false);
                string bridgeEnvVar = global::XREngine.Engine.Rendering.VulkanUpscaleBridgeEnvVar;

                reason.ShouldContain($"experimental bridge disabled (set {bridgeEnvVar}=1 to opt in)");
                reason.ShouldContain("GL_EXT_memory_object is unavailable");
                reason.ShouldContain("GL_EXT_memory_object_win32 is unavailable");
                reason.ShouldContain("GL_EXT_semaphore is unavailable");
                reason.ShouldContain("GL_EXT_semaphore_win32 is unavailable");
                reason.ShouldContain("probe failed during test");
                reason.ShouldContain("bridge vendor dispatch also requires a compatible DLSS or XeSS runtime plus per-vendor support checks");
            });
    }

    [Test]
    public void UnavailabilityDescription_ReportsHdrAndCrossGpuMismatch()
    {
        WithBridgeSnapshot(
            new global::XREngine.VulkanUpscaleBridgeCapabilitySnapshot
            {
                EnvironmentEnabled = true,
                WindowsOnly = false,
                MonoViewportOnly = false,
                HdrSupported = false,
                HasOpenGlExternalMemory = true,
                HasOpenGlExternalMemoryWin32 = true,
                HasOpenGlSemaphore = true,
                HasOpenGlSemaphoreWin32 = true,
                VulkanProbeSucceeded = true,
                HasVulkanExternalMemoryImport = true,
                HasVulkanExternalSemaphoreImport = true,
                SamePhysicalGpu = false,
                GpuIdentityReason = "cross-adapter imports are blocked",
            },
            () =>
            {
                string reason = global::XREngine.Engine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(null, hdrRequested: true);

                reason.ShouldContain("bridge MVP is SDR only");
                reason.ShouldContain("cross-adapter imports are blocked");
            });
    }

    [Test]
    public void DefaultRenderPipelines_MapVendorUpscaleSourcesToExplicitTextures()
    {
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), DefaultRenderPipeline.PostProcessOutputFBOName, DefaultRenderPipeline.PostProcessOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), DefaultRenderPipeline.FxaaFBOName, DefaultRenderPipeline.FxaaOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), DefaultRenderPipeline.SmaaFBOName, DefaultRenderPipeline.SmaaOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), DefaultRenderPipeline.TsrUpscaleFBOName, DefaultRenderPipeline.FxaaOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), "UnknownFbo", null);

        AssertSourceTextureMapping(typeof(DefaultRenderPipeline2), DefaultRenderPipeline2.PostProcessOutputFBOName, DefaultRenderPipeline2.PostProcessOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline2), DefaultRenderPipeline2.FxaaFBOName, DefaultRenderPipeline2.FxaaOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline2), DefaultRenderPipeline2.SmaaFBOName, DefaultRenderPipeline2.SmaaOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline2), DefaultRenderPipeline2.TsrUpscaleFBOName, DefaultRenderPipeline2.FxaaOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline2), "UnknownFbo", null);
    }

    [Test]
    public void DefaultRenderPipelines_ThreadDepthAndMotionResourcesIntoVendorUpscale()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("vendorBlit.SourceTextureName = ResolveVendorUpscaleSourceTextureName(sourceFboName);");
        pipelineSource.ShouldContain("vendorBlit.DepthTextureName = DepthViewTextureName;");
        pipelineSource.ShouldContain("vendorBlit.DepthStencilTextureName = DepthStencilTextureName;");
        pipelineSource.ShouldContain("vendorBlit.MotionTextureName = VelocityTextureName;");
        pipelineSource.ShouldContain("vendorBlit.MotionFrameBufferName = VelocityFBOName;");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("vendorBlit.SourceTextureName = ResolveVendorUpscaleSourceTextureName(sourceFboName);");
        pipeline2Source.ShouldContain("vendorBlit.DepthTextureName = DepthViewTextureName;");
        pipeline2Source.ShouldContain("vendorBlit.DepthStencilTextureName = DepthStencilTextureName;");
        pipeline2Source.ShouldContain("vendorBlit.MotionTextureName = VelocityTextureName;");
        pipeline2Source.ShouldContain("vendorBlit.MotionFrameBufferName = VelocityFBOName;");
    }

    [Test]
    public void VendorUpscaleBridgeDispatch_UsesExposureDepthAndMotionNormalizationContracts()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs").Replace("\r\n", "\n");
        source.ShouldContain("private const float BridgeMotionVectorNormalizationScale = 0.5f;");
        source.ShouldContain("public string AutoExposureTextureName { get; set; } = DefaultRenderPipeline.AutoExposureTextureName;");
        source.ShouldContain("bool hasExposureTexture = sourceExposureFbo is not null;");
        source.ShouldContain("FrameIndex = unchecked((uint)Math.Max(0L, renderer._frameCounter)),");
        source.ShouldContain("ReverseDepth = camera.IsReversedDepth,");
        source.ShouldContain("HasExposureTexture = hasExposureTexture,");
        source.ShouldContain("MotionVectorScaleX = BridgeMotionVectorNormalizationScale,");
        source.ShouldContain("MotionVectorScaleY = BridgeMotionVectorNormalizationScale,");
    }

    [Test]
    public void VulkanUpscaleBridgeSidecar_LocksSharedImageFormatsForMvp()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/VulkanUpscaleBridgeSidecar.cs").Replace("\r\n", "\n");
        source.ShouldContain("private const int FramesInFlight = 2;");
        source.ShouldContain("EPixelInternalFormat.Rgba16f,");
        source.ShouldContain("EPixelInternalFormat.Depth24Stencil8,");
        source.ShouldContain("EPixelInternalFormat.RG16f,");
        source.ShouldContain("EPixelInternalFormat.R32f,");
        source.ShouldContain("outputHdr ? EPixelInternalFormat.Rgba16f : EPixelInternalFormat.Rgba8,");
        source.ShouldContain("EPixelInternalFormat.RG16f => Format.R16G16Sfloat,");
        source.ShouldContain("EPixelInternalFormat.R32f => Format.R32Sfloat,");
        source.ShouldContain("EPixelInternalFormat.Depth24Stencil8 => Format.D24UnormS8Uint,");
    }

    [Test]
    public void VendorSdkBridgeBindings_StayOnImportedImagesWithoutSwapchainPresent()
    {
        string dlssSource = ReadWorkspaceFile("XRENGINE/Rendering/DLSS/StreamlineNative.cs").Replace("\r\n", "\n");
        dlssSource.ShouldContain("Device = sidecar.Device,");
        dlssSource.ShouldContain("Instance = sidecar.Instance,");
        dlssSource.ShouldContain("PhysicalDevice = sidecar.PhysicalDevice,");
        dlssSource.ShouldContain("BufferTypeExposure = 13;");
        dlssSource.ShouldContain("MotionVectorScale = new StreamlineFloat2(parameters.MotionVectorScaleX, parameters.MotionVectorScaleY),");
        dlssSource.ShouldContain("DepthInverted = parameters.ReverseDepth ? StreamlineBoolean.True : StreamlineBoolean.False,");
        dlssSource.ShouldNotContain("Swapchain");

        string xessSource = ReadWorkspaceFile("XRENGINE/Rendering/XeSS/IntelXessNative.cs").Replace("\r\n", "\n");
        xessSource.ShouldContain("XessResult velocityScaleResult = _setVelocityScale(_context, parameters.MotionVectorScaleX, parameters.MotionVectorScaleY);");
        xessSource.ShouldContain("ExposureScaleTexture = parameters.HasExposureTexture ? CreateImageViewInfo(slot.Exposure) : default,");
        xessSource.ShouldContain("ResponsivePixelMaskTexture = default,");
        xessSource.ShouldNotContain("Swapchain");
    }

    [Test]
    public void BridgeLifetimeAndFallbackContracts_ArePresent()
    {
        string probeSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/VulkanUpscaleBridgeProbe.cs").Replace("\r\n", "\n");
        probeSource.ShouldContain("\"VK_KHR_external_memory\"");
        probeSource.ShouldContain("\"VK_KHR_external_semaphore\"");
        probeSource.ShouldContain("\"VK_KHR_external_memory_win32\"");
        probeSource.ShouldContain("\"VK_KHR_external_semaphore_win32\"");
        probeSource.ShouldContain("ProbeFailureReason = selected.SupportsBridgeImport");

        string bridgeSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/VulkanUpscaleBridge.cs").Replace("\r\n", "\n");
        bridgeSource.ShouldContain("_viewport.Resized += HandleViewportResized;");
        bridgeSource.ShouldContain("_viewport.InternalResolutionResized += HandleInternalResolutionResized;");
        bridgeSource.ShouldContain("private void HandleViewportResized(XRViewport _)\n        => MarkNeedsRecreate(ViewportResizeReason);");
        bridgeSource.ShouldContain("private void HandleInternalResolutionResized(XRViewport _)\n        => MarkNeedsRecreate(InternalResolutionResizeReason);");

        string engineRenderingSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.cs").Replace("\r\n", "\n");
        engineRenderingSource.ShouldContain("InvalidateAllVulkanUpscaleBridges(\"anti-aliasing settings changed\");");
        engineRenderingSource.ShouldContain("NotifyVulkanUpscaleBridgeVendorSelectionChanged(\"NVIDIA DLSS preference changed\");");
        engineRenderingSource.ShouldContain("NotifyVulkanUpscaleBridgeVendorSelectionChanged(\"Intel XeSS preference changed\");");

        string vendorUpscaleSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs").Replace("\r\n", "\n");
        vendorUpscaleSource.ShouldContain("else if (viewport?.Window?.Renderer is OpenGLRenderer openGlRenderer &&");
        vendorUpscaleSource.ShouldContain("if (TryRunBridge(openGlRenderer, viewport, sourceFrameBuffer, resolvedColorTexture, out string bridgeFailure))");
        vendorUpscaleSource.ShouldContain("ReportBridgeFallback(viewport, bridgeFailure);");
        vendorUpscaleSource.ShouldContain("Engine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(viewport, ActivePipelineInstance.EffectiveOutputHDRThisFrame ?? false)");
    }

    private static void AssertSourceTextureMapping(Type pipelineType, string sourceFboName, string? expectedTextureName)
    {
        MethodInfo method = pipelineType.GetMethod("ResolveVendorUpscaleSourceTextureName", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(pipelineType.FullName, "ResolveVendorUpscaleSourceTextureName");

        object? result = method.Invoke(null, [sourceFboName]);
        result.ShouldBe(expectedTextureName);
    }

    private static void WithBridgeSnapshot(global::XREngine.VulkanUpscaleBridgeCapabilitySnapshot snapshot, Action action)
    {
        Type renderingType = typeof(global::XREngine.Engine).GetNestedType("Rendering", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not resolve Engine.Rendering nested type.");
        FieldInfo snapshotField = renderingType.GetField("_vulkanUpscaleBridgeSnapshot", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(renderingType.FullName, "_vulkanUpscaleBridgeSnapshot");
        FieldInfo snapshotSyncField = renderingType.GetField("_vulkanUpscaleBridgeSnapshotSync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(renderingType.FullName, "_vulkanUpscaleBridgeSnapshotSync");

        object sync = snapshotSyncField.GetValue(null) ?? throw new InvalidOperationException("Bridge snapshot sync object was null.");
        object? original;

        lock (sync)
        {
            original = snapshotField.GetValue(null);
            snapshotField.SetValue(null, snapshot);
        }

        try
        {
            action();
        }
        finally
        {
            lock (sync)
                snapshotField.SetValue(null, original);
        }
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