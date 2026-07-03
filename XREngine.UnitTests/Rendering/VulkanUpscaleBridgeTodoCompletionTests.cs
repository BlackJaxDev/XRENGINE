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

                reason.ShouldContain($"{bridgeEnvVar}=0 disabled the OpenGL->Vulkan upscale bridge");
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

                reason.ShouldContain("bridge HDR output is unavailable");
                reason.ShouldContain("cross-adapter imports are blocked");
            });
    }

    [Test]
    public void DefaultRenderPipelines_MapVendorUpscaleSourcesToExplicitTextures()
    {
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), DefaultRenderPipeline.PostProcessOutputFBOName, DefaultRenderPipeline.PostProcessOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), DefaultRenderPipeline.FxaaFBOName, DefaultRenderPipeline.FxaaOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), DefaultRenderPipeline.SmaaFBOName, DefaultRenderPipeline.SmaaOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), DefaultRenderPipeline.TsrUpscaleFBOName, DefaultRenderPipeline.TsrOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), "UnknownFbo", null);

        AssertSourceTextureMapping(typeof(DefaultRenderPipeline2), DefaultRenderPipeline2.PostProcessOutputFBOName, DefaultRenderPipeline2.PostProcessOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline2), DefaultRenderPipeline2.FxaaFBOName, DefaultRenderPipeline2.FxaaOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline2), DefaultRenderPipeline2.SmaaFBOName, DefaultRenderPipeline2.SmaaOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline2), DefaultRenderPipeline2.TsrUpscaleFBOName, DefaultRenderPipeline2.TsrOutputTextureName);
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
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeSidecar.cs").Replace("\r\n", "\n");
        source.ShouldContain("private const int FramesInFlight = 2;");
        source.ShouldContain("EPixelInternalFormat.Rgba16f,");
        source.ShouldContain("EPixelInternalFormat.Depth24Stencil8,");
        source.ShouldContain("EPixelInternalFormat.RG16f,");
        source.ShouldContain("EPixelInternalFormat.R32f,");
        source.ShouldContain("outputHdr ? EPixelInternalFormat.Rgba16f : EPixelInternalFormat.Rgba8,");
        source.ShouldContain("outputHdr ? EPixelType.HalfFloat : EPixelType.UnsignedByte,");
        source.ShouldContain("outputHdr ? ESizedInternalFormat.Rgba16f : ESizedInternalFormat.Rgba8,");
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
        dlssSource.ShouldContain("ColorBuffersHdr = parameters.OutputHdr ? StreamlineBoolean.True : StreamlineBoolean.False,");
        dlssSource.ShouldContain("if (parameters.InputWidth == parameters.OutputWidth && parameters.InputHeight == parameters.OutputHeight)\n                        return StreamlineDlssMode.Dlaa;");
        dlssSource.ShouldContain("EDlssQualityMode.UltraQuality => StreamlineDlssMode.MaxQuality,");
        dlssSource.ShouldNotContain("=> StreamlineDlssMode.UltraQuality");
        dlssSource.ShouldContain("LogMessageCallback = LogMessageCallbackPtr,");
        dlssSource.ShouldContain("StreamlineResourceLifecycle.ValidUntilEvaluate, inputExtent");
        dlssSource.ShouldContain("private delegate StreamlineResult SlDlssGetOptimalSettingsDelegate(ref StreamlineDlssOptions options, ref StreamlineDlssOptimalSettings settings);");
        dlssSource.ShouldContain("DLSS optimal input={settings.OptimalRenderWidth}x{settings.OptimalRenderHeight}");
        dlssSource.ShouldNotContain("StreamlineResult allocateResult = _allocateResources");
        int bridgeSessionIndex = dlssSource.IndexOf("internal sealed class BridgeSession", StringComparison.Ordinal);
        bridgeSessionIndex.ShouldBeGreaterThanOrEqualTo(0);
        int bridgeSessionEndIndex = dlssSource.IndexOf("private static IntPtr ToIntPtr", bridgeSessionIndex, StringComparison.Ordinal);
        bridgeSessionEndIndex.ShouldBeGreaterThan(bridgeSessionIndex);
        string bridgeSessionSource = dlssSource[bridgeSessionIndex..bridgeSessionEndIndex];
        bridgeSessionSource.IndexOf("StreamlineResult setOptionsResult = _setOptions(ref viewport, ref options);", StringComparison.Ordinal)
            .ShouldBeLessThan(bridgeSessionSource.IndexOf("StreamlineResult tagResult = _setTagForFrame(frameToken, ref viewport, (IntPtr)tags, tagCount, commandBuffer);", StringComparison.Ordinal));
        bridgeSessionSource.IndexOf("StreamlineResult tagResult = _setTagForFrame(frameToken, ref viewport, (IntPtr)tags, tagCount, commandBuffer);", StringComparison.Ordinal)
            .ShouldBeLessThan(bridgeSessionSource.IndexOf("StreamlineResult constantsResult = _setConstants(ref constants, frameToken, ref viewport);", StringComparison.Ordinal));
        bridgeSessionSource.IndexOf("StreamlineResult constantsResult = _setConstants(ref constants, frameToken, ref viewport);", StringComparison.Ordinal)
            .ShouldBeLessThan(bridgeSessionSource.IndexOf("StreamlineResult evaluateResult = _evaluateFeature(FeatureDlss, frameToken, (IntPtr)inputs, 1, commandBuffer);", StringComparison.Ordinal));
        bridgeSessionSource.ShouldNotContain("Swapchain");

        string xessSource = ReadWorkspaceFile("XRENGINE/Rendering/XeSS/IntelXessNative.cs").Replace("\r\n", "\n");
        xessSource.ShouldContain("if (!parameters.OutputHdr)");
        xessSource.ShouldContain("XessResult velocityScaleResult = _setVelocityScale(_context, parameters.MotionVectorScaleX, parameters.MotionVectorScaleY);");
        xessSource.ShouldContain("ExposureScaleTexture = parameters.HasExposureTexture ? CreateImageViewInfo(slot.Exposure) : default,");
        xessSource.ShouldContain("ResponsivePixelMaskTexture = default,");
        xessSource.ShouldNotContain("Swapchain");
    }

    [Test]
    public void NativeVulkanDlssSr_QueuesStreamlineIntoTheMainCommandBuffer()
    {
        string vendorSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs").Replace("\r\n", "\n");
        vendorSource.ShouldContain("TryResolveNativeDlssDepthTexture");
        vendorSource.ShouldContain("TryResolveNativeDlssMotionTexture");
        vendorSource.ShouldContain("ResolveNativeDlssExposureTexture");
        vendorSource.ShouldContain("ValidateNativeDlssInputSizes(sourceColorTexture, depthTexture, motionTexture, outputWidth, outputHeight");
        vendorSource.ShouldContain("TryEnsureNativeDlssOutputTexture(outputWidth, outputHeight, outputHdr");
        vendorSource.ShouldContain("TryEnsureNativeDlssSession(renderer, viewport");
        vendorSource.ShouldContain("TryEnsureNativeDlssFrameGenerationSession(renderer, viewport");
        vendorSource.ShouldContain("if (frameGenRequested && !TryEnsureNativeDlssFrameGenerationSession(renderer, viewport");
        vendorSource.ShouldNotContain("if (!dlssRequested && frameGenRequested && !TryEnsureNativeDlssFrameGenerationSession");
        vendorSource.ShouldContain("renderer.TryResolveStreamlineImage(sourceColorTexture, depthOnly: false");
        vendorSource.ShouldContain("renderer.EnqueueDlssUpscale(");
        vendorSource.ShouldContain("renderer.EnqueueDlssFrameGeneration(");
        vendorSource.ShouldContain("RememberNativeDlssDispatch(camera, dispatchParameters);");
        vendorSource.ShouldContain("NVIDIA DLSS frame generation failed");
        vendorSource.ShouldContain("NvidiaDlssManager.Native.TryDispatchFrameGeneration(");
        vendorSource.ShouldContain("VulkanRenderer.VulkanStreamlineImage hudlessImage = dlssRequested");
        vendorSource.ShouldContain("in dispatchParameters");
        vendorSource.ShouldContain("in depthImage");
        vendorSource.ShouldContain("in motionImage");
        vendorSource.ShouldContain("in hudlessImage");
        vendorSource.ShouldContain("NVIDIA DLSS frame generation requires a HUD-less color buffer matching the backbuffer.");
        int dlssUpscaleEnqueueIndex = vendorSource.IndexOf("renderer.EnqueueDlssUpscale(", StringComparison.Ordinal);
        int dlssFrameGenerationAfterUpscaleIndex = vendorSource.IndexOf(
            "renderer.EnqueueDlssFrameGeneration(\n                    passIndex,\n                    _nativeDlssFrameGenerationSession!,\n                    depthImage,\n                    motionImage,\n                    outputImage,\n                    dispatchParameters);",
            dlssUpscaleEnqueueIndex,
            StringComparison.Ordinal);
        dlssUpscaleEnqueueIndex.ShouldBeGreaterThanOrEqualTo(0);
        dlssFrameGenerationAfterUpscaleIndex.ShouldBeGreaterThan(dlssUpscaleEnqueueIndex);

        string interopSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanRenderer.StreamlineInterop.cs").Replace("\r\n", "\n");
        interopSource.ShouldContain("internal readonly record struct VulkanStreamlineImage");
        interopSource.ShouldContain("GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource source");
        interopSource.ShouldContain("DeviceMemory memory = source.DescriptorMemory;");
        interopSource.ShouldContain("internal void EnqueueDlssUpscale(");
        interopSource.ShouldContain("EnqueueFrameOp(new DlssUpscaleOp(");
        interopSource.ShouldContain("internal void EnqueueDlssFrameGeneration(");
        interopSource.ShouldContain("EnqueueFrameOp(new DlssFrameGenerationOp(");

        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanRenderer.CommandBuffers.Dlss.cs").Replace("\r\n", "\n");
        commandBufferSource.ShouldContain("private void RecordDlssUpscaleOp(CommandBuffer commandBuffer, DlssUpscaleOp op)");
        commandBufferSource.ShouldContain("TransitionStreamlineImageToGeneral(commandBuffer, op.SourceColor)");
        commandBufferSource.ShouldContain("NvidiaDlssManager.Native.TryRecordNativeVulkanUpscale(");
        commandBufferSource.ShouldContain("Debug.RenderingError($\"Requested NVIDIA DLSS upscale failed during Vulkan command recording");
        commandBufferSource.ShouldContain("private void RecordDlssFrameGenerationOp(CommandBuffer commandBuffer, DlssFrameGenerationOp op)");
        commandBufferSource.ShouldContain("TransitionStreamlineImageToGeneral(commandBuffer, op.HudlessColor)");
        commandBufferSource.ShouldContain("NvidiaDlssManager.Native.TryRecordNativeVulkanFrameGeneration(");
        commandBufferSource.ShouldContain("Debug.RenderingError($\"Requested NVIDIA DLSS frame generation failed during Vulkan command recording");

        string meshRendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs").Replace("\r\n", "\n");
        meshRendererSource.ShouldContain("internal sealed record DlssUpscaleOp(");
        meshRendererSource.ShouldContain("internal sealed record DlssFrameGenerationOp(");
        meshRendererSource.ShouldContain("DlssUpscaleOp dlssUpscale => dlssUpscale with { PassIndex = validatedPassIndex },");
        meshRendererSource.ShouldContain("DlssFrameGenerationOp dlssFrameGeneration => dlssFrameGeneration with { PassIndex = validatedPassIndex },");

        string streamlineSource = ReadWorkspaceFile("XRENGINE/Rendering/DLSS/StreamlineNative.cs").Replace("\r\n", "\n");
        streamlineSource.ShouldContain("internal static bool TryCreateNativeVulkanSession(");
        streamlineSource.ShouldContain("internal static bool TryRecordNativeVulkanUpscale(");
        streamlineSource.ShouldContain("internal sealed class NativeVulkanSession : IDisposable");
        streamlineSource.ShouldContain("internal static bool TryCreateNativeFrameGenerationSession(");
        streamlineSource.ShouldContain("internal static bool TryRecordNativeVulkanFrameGeneration(");
        streamlineSource.ShouldContain("internal sealed class NativeFrameGenerationSession : IDisposable");
        streamlineSource.ShouldContain("Device = renderer.Device,");
        streamlineSource.ShouldContain("StreamlineResource CreateResource(in VulkanRenderer.VulkanStreamlineImage image");
        streamlineSource.ShouldContain("StreamlineResult evaluateResult = _evaluateFeature(FeatureDlss, frameToken, (IntPtr)inputs, 1, commandBufferPtr);");
        streamlineSource.ShouldContain("private const ulong StreamlineSdkVersion = 0x0002000B0001FEDC;");
        streamlineSource.ShouldContain("FeatureDlssG = 1000;");
        streamlineSource.ShouldContain("FeatureReflex = 3;");
        streamlineSource.ShouldContain("FeaturePcl = 4;");
        streamlineSource.ShouldContain("BufferTypeHudLessColor = 2;");
        streamlineSource.ShouldContain("slDLSSGSetOptions");
        streamlineSource.ShouldContain("slDLSSGGetState");
        streamlineSource.ShouldContain("slReflexSetOptions");
        streamlineSource.ShouldContain("slPCLSetMarker");
        streamlineSource.ShouldContain("EnsureFrameGenerationRequirements");
        streamlineSource.ShouldContain("StreamlineResourceLifecycle.ValidUntilPresent, outputExtent");
        streamlineSource.ShouldContain("TryDisableFrameGeneration");
        streamlineSource.ShouldContain("TryCreateProxySwapchain");
        streamlineSource.ShouldContain("TryAcquireProxyNextImage");
        streamlineSource.ShouldContain("TryQueueProxyPresent");

        string swapchainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs").Replace("\r\n", "\n");
        swapchainSource.ShouldContain("DlssFrameGenerationHdrSurfacePreferences");
        swapchainSource.ShouldContain("DisableStreamlineFrameGenerationBeforeSwapchainMutation");
        swapchainSource.ShouldContain("NvidiaDlssManager.Native.TryDisableFrameGeneration");
        swapchainSource.ShouldContain("Vulkan VSync with DLSS-G is not supported by Streamline.");
    }

    [Test]
    public void DefaultRenderPipelines_RequestVendorRenderScaleEveryFrame()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("if (!IsRenderingExternalSwapchainTarget() && TryResolveVendorInternalResolutionScale(out float vendorScale))\n            return vendorScale;");
        pipelineSource.ShouldContain("if (mode == EAntiAliasingMode.Dlaa)\n            return 1.0f;");
        pipelineSource.ShouldContain("RequestedInternalResolution = 1.0f;");
        pipelineSource.ShouldContain("RequestedInternalResolution = vendorScale;");
        pipelineSource.ShouldContain("NvidiaDlssManager.GetRecommendedRenderScale(RuntimeEngine.Rendering.Settings)");
        pipelineSource.ShouldContain("IntelXessManager.GetRecommendedRenderScale(RuntimeEngine.Rendering.Settings)");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("if (!IsRenderingExternalSwapchainTarget() && TryResolveVendorInternalResolutionScale(out float vendorScale))\n            return vendorScale;");
        pipeline2Source.ShouldContain("if (mode == EAntiAliasingMode.Dlaa)\n            return 1.0f;");
        pipeline2Source.ShouldContain("RequestedInternalResolution = 1.0f;");
        pipeline2Source.ShouldContain("RequestedInternalResolution = vendorScale;");
        pipeline2Source.ShouldContain("NvidiaDlssManager.GetRecommendedRenderScale(RuntimeEngine.Rendering.Settings)");
        pipeline2Source.ShouldContain("IntelXessManager.GetRecommendedRenderScale(RuntimeEngine.Rendering.Settings)");

        string dlssManagerSource = ReadWorkspaceFile("XRENGINE/Rendering/DLSS/NvidiaDlssManager.cs").Replace("\r\n", "\n");
        dlssManagerSource.ShouldContain("internal static float GetRecommendedRenderScale(object? settings = null)");
        dlssManagerSource.ShouldContain("if (RuntimeEngine.EffectiveSettings.AntiAliasingMode == EAntiAliasingMode.Dlaa)\n                return MaxScale;");
        dlssManagerSource.ShouldContain("if (!RuntimeEngine.EffectiveSettings.EnableNvidiaDlss || !_cachedIsSupported)");

        string xessManagerSource = ReadWorkspaceFile("XRENGINE/Rendering/XeSS/IntelXessManager.cs").Replace("\r\n", "\n");
        xessManagerSource.ShouldContain("internal static float GetRecommendedRenderScale(object? settings = null)");
    }

    [Test]
    public void VendorUpscaleRuntime_SkipsPostAaUpscaleAndHonorsExplicitSourceTexture()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("private static bool RuntimeEnableVendorUpscale");
        pipelineSource.ShouldContain("|| ResolveAntiAliasingMode() == EAntiAliasingMode.Dlaa");
        pipelineSource.ShouldContain("private static bool RuntimeEnableFxaa\n        => !RuntimeEnableVendorUpscale && ResolveAntiAliasingMode() == EAntiAliasingMode.Fxaa;");
        pipelineSource.ShouldContain("private static bool RuntimeEnableSmaa\n        => !RuntimeEnableVendorUpscale && ResolveAntiAliasingMode() == EAntiAliasingMode.Smaa;");
        pipelineSource.ShouldContain("private static bool RuntimeNeedsTsrUpscale\n        => !RuntimeEnableVendorUpscale\n        && !DisableHistoryBasedVrEffects()\n        && ResolveAntiAliasingMode() == EAntiAliasingMode.Tsr;");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("private static bool RuntimeEnableVendorUpscale");
        pipeline2Source.ShouldContain("|| ResolveAntiAliasingMode() == EAntiAliasingMode.Dlaa");
        pipeline2Source.ShouldContain("private static bool RuntimeEnableFxaa\n        => !RuntimeEnableVendorUpscale && ResolveAntiAliasingMode() == EAntiAliasingMode.Fxaa;");
        pipeline2Source.ShouldContain("private static bool RuntimeEnableSmaa\n        => !RuntimeEnableVendorUpscale && ResolveAntiAliasingMode() == EAntiAliasingMode.Smaa;");
        pipeline2Source.ShouldContain("private static bool RuntimeNeedsTsrUpscale\n        => !RuntimeEnableVendorUpscale\n        && !DisableHistoryBasedVrEffects()\n        && ResolveAntiAliasingMode() == EAntiAliasingMode.Tsr;");

        string vendorUpscaleSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs").Replace("\r\n", "\n");
        vendorUpscaleSource.ShouldContain("private static bool IsNvidiaDlssFeatureRequested()");
        vendorUpscaleSource.ShouldContain("private static bool IsNvidiaDlaaRequested()");
        vendorUpscaleSource.ShouldContain("if (IsNvidiaDlaaRequested())\n                return TryRunDlss(out failureReason);");
        vendorUpscaleSource.ShouldContain("=> RenderPipeline.ResolveEffectiveAntiAliasingModeForFrame() == EAntiAliasingMode.Dlaa;");
        int explicitSourceIndex = vendorUpscaleSource.IndexOf("if (!string.IsNullOrWhiteSpace(SourceTextureName))", StringComparison.Ordinal);
        int sourceFboIndex = vendorUpscaleSource.IndexOf("if (sourceFrameBuffer is not null && FrameBufferHasColorAttachment(sourceFrameBuffer))", StringComparison.Ordinal);

        explicitSourceIndex.ShouldBeGreaterThanOrEqualTo(0);
        sourceFboIndex.ShouldBeGreaterThanOrEqualTo(0);
        explicitSourceIndex.ShouldBeLessThan(sourceFboIndex);
    }

    [Test]
    public void BridgeLifetimeAndFallbackContracts_ArePresent()
    {
        string probeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeProbe.cs").Replace("\r\n", "\n");
        probeSource.ShouldContain("\"VK_KHR_external_memory\"");
        probeSource.ShouldContain("\"VK_KHR_external_semaphore\"");
        probeSource.ShouldContain("\"VK_KHR_external_memory_win32\"");
        probeSource.ShouldContain("\"VK_KHR_external_semaphore_win32\"");
        probeSource.ShouldContain("ProbeFailureReason = selected.SupportsBridgeImport");

        string bridgeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridge.cs").Replace("\r\n", "\n");
        bridgeSource.ShouldContain("_viewport.Resized += HandleViewportResized;");
        bridgeSource.ShouldContain("_viewport.InternalResolutionResized += HandleInternalResolutionResized;");
        bridgeSource.ShouldContain("private void HandleViewportResized(XRViewport _)\n        => MarkNeedsRecreate(ViewportResizeReason);");
        bridgeSource.ShouldContain("private void HandleInternalResolutionResized(XRViewport _)\n        => MarkNeedsRecreate(InternalResolutionResizeReason);");
        bridgeSource.ShouldContain("private readonly HashSet<string> _loggedStateFingerprints = [];");
        bridgeSource.ShouldContain("if (!_loggedStateFingerprints.Add(logFingerprint))");
        bridgeSource.ShouldContain("CanRecreateFrameSlotsInPlace(");
        bridgeSource.ShouldContain("IsFrameSlotOnlyRecreateReason(recreateReason)");
        bridgeSource.ShouldContain("string.Equals(recreateReason, \"output HDR changed\", StringComparison.Ordinal)");
        bridgeSource.ShouldContain("_sidecar.RecreateFrameSlots(renderer, frameResources, SanitizeLabel(DescribeViewport()))");

        string sidecarSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeSidecar.cs").Replace("\r\n", "\n");
        sidecarSource.ShouldContain("public VulkanUpscaleBridgeFrameSlot[] RecreateFrameSlots(OpenGLRenderer renderer, VulkanUpscaleBridgeFrameResources frameResources, string viewportTag)");
        sidecarSource.ShouldContain("ResetVendorSessionsForFrameResourceRecreate();");
        sidecarSource.ShouldContain("_dlssSession?.ResetResources();");

        string dlssSource = ReadWorkspaceFile("XRENGINE/Rendering/DLSS/StreamlineNative.cs").Replace("\r\n", "\n");
        dlssSource.ShouldContain("public void ResetResources()");
        dlssSource.ShouldContain("FreeAllocatedResources();");

        string bridgeEnvSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.VulkanUpscaleBridge.cs").Replace("\r\n", "\n");
        bridgeEnvSource.ShouldContain("public static bool VulkanUpscaleBridgeRequested => IsEnvFlagEnabled(VulkanUpscaleBridgeEnvVar, defaultValue: true);");
        bridgeEnvSource.ShouldContain("public static bool VulkanUpscaleBridgeHdrSupported => true;");
        bridgeEnvSource.ShouldContain("EVulkanUpscaleBridgeSurfaceSet.Exposure;");
        bridgeEnvSource.ShouldContain("disabled the OpenGL->Vulkan upscale bridge");

        string engineRenderingSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.cs").Replace("\r\n", "\n");
        engineRenderingSource.ShouldContain("InvalidateAllVulkanUpscaleBridges(\"anti-aliasing settings changed\");");
        engineRenderingSource.ShouldContain("NotifyVulkanUpscaleBridgeVendorSelectionChanged(\"NVIDIA DLSS preference changed\");");
        engineRenderingSource.ShouldContain("NotifyVulkanUpscaleBridgeVendorSelectionChanged(\"Intel XeSS preference changed\");");
        engineRenderingSource.ShouldContain("RefreshWindowsAfterVendorUpscalePreferenceChanged();");
        engineRenderingSource.ShouldContain("window.InvalidateScenePanelResources();");
        engineRenderingSource.ShouldContain("window.RequestRenderStateRecheck(resetCircuitBreaker: true);");
        engineRenderingSource.ShouldContain("FrameGenerationRequested={8} FrameGenerationAvailable={9} FrameGenerationUnavailableReason={10}");
        engineRenderingSource.ShouldContain("NvidiaDlssFrameGenerationMode is Off");
        engineRenderingSource.ShouldContain("Frame generation is requested, but unavailable");

        string vendorUpscaleSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs").Replace("\r\n", "\n");
        vendorUpscaleSource.ShouldContain("viewport?.Window?.Renderer is OpenGLRenderer openGlRenderer &&");
        vendorUpscaleSource.ShouldContain("if (TryRunBridge(openGlRenderer, viewport, sourceFrameBuffer, resolvedColorTexture, out string bridgeFailure))");
        vendorUpscaleSource.ShouldContain("FailRequestedVendorFeature(\"OpenGL-to-Vulkan vendor upscale bridge\", bridgeFailure);");
        vendorUpscaleSource.ShouldContain("ShouldRecreateBridgeAfterDispatchFailure(bridgeFailure)");
        vendorUpscaleSource.ShouldContain("RuntimeEngine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(viewport, ActivePipelineInstance.EffectiveOutputHDRThisFrame ?? false)");
        vendorUpscaleSource.ShouldContain("requires Vulkan or the OpenGL->Vulkan upscale bridge");
        vendorUpscaleSource.ShouldContain("No fallback blit will be rendered because a vendor upscaler or frame-generation mode was explicitly requested.");
        vendorUpscaleSource.ShouldNotContain("experimental OpenGL->Vulkan bridge");
    }

    [Test]
    public void SparseTextureInitialPromotions_UseSynchronousExposurePath()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/GLTexture2D.SparseStreaming.Async.cs").Replace("\r\n", "\n");

        source.ShouldContain("bool hasPreviousCommit = previousCommittedBaseMipLevel != int.MaxValue;");
        source.ShouldContain("bool isDemotion = hasPreviousCommit && committedBaseMipLevel > previousCommittedBaseMipLevel;");
        source.ShouldContain("if (!hasPreviousCommit)");
        source.ShouldContain("if (isDemotion)");

        string hostSource = ReadWorkspaceFile("XRENGINE/Engine/Engine.RuntimeRenderingHostServices.cs").Replace("\r\n", "\n");
        hostSource.ShouldContain("void CompleteAsyncTransition(SparseTextureStreamingTransitionResult result)");
        hostSource.ShouldContain("XRTexture2D.CompleteSparseTransition");

        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs").Replace("\r\n", "\n");
        managerSource.ShouldContain("private int _sparseFinalizeScheduled;");
        managerSource.ShouldContain("if (!RuntimeRenderingHostServices.Current.IsRenderThread)");
        managerSource.ShouldContain("TextureStreaming.FinalizeSparseTransitions");
    }

    [Test]
    public void WindowCloseRequests_AreDeferredToTheFrameBoundary()
    {
        string windowSource = ReadWorkspaceFile("XRENGINE/Rendering/API/XRWindow.cs").Replace("\r\n", "\n");
        string editorSource = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.ImGui.cs").Replace("\r\n", "\n");
        string engineLifecycleSource = ReadWorkspaceFile("XRENGINE/Engine/Engine.Lifecycle.cs").Replace("\r\n", "\n");

        windowSource.ShouldContain("private int _pendingCloseRequested;");
        windowSource.ShouldContain("public void RequestClose()");
        windowSource.ShouldContain("if (RuntimeEngine.IsDispatchingRenderFrame)");
        windowSource.ShouldContain("ProcessDeferredCloseRequest();");
        editorSource.ShouldContain("window.RequestClose();");
        engineLifecycleSource.ShouldContain("window.RequestClose();");
    }

    [Test]
    public void OpenGlRenderer_LoadsBridgeInteropBeforeFirstCapabilitySnapshot()
    {
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Bootstrap/OpenGLRenderer.cs").Replace("\r\n", "\n");

        int extMemoryIndex = rendererSource.IndexOf("EXTMemoryObject = ESApi.TryGetExtension<ExtMemoryObject>(out var ext) ? ext : null;", StringComparison.Ordinal);
        int extSemaphoreWin32Index = rendererSource.IndexOf("EXTSemaphoreWin32 = ESApi.TryGetExtension<ExtSemaphoreWin32>(out var ext4) ? ext4 : null;", StringComparison.Ordinal);
        int apiIndex = rendererSource.IndexOf("var api = Api;", StringComparison.Ordinal);

        extMemoryIndex.ShouldBeGreaterThanOrEqualTo(0);
        extSemaphoreWin32Index.ShouldBeGreaterThanOrEqualTo(0);
        apiIndex.ShouldBeGreaterThanOrEqualTo(0);
        extMemoryIndex.ShouldBeLessThan(apiIndex);
        extSemaphoreWin32Index.ShouldBeLessThan(apiIndex);
    }

    [Test]
    public void ExternalMemoryImports_UseExplicitMipLevelContract()
    {
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/XRTexture.cs").Replace("\r\n", "\n");
        textureSource.ShouldContain("private uint _openGlExternalMemoryImportMipLevels = 1;");
        textureSource.ShouldContain("public uint OpenGlExternalMemoryImportMipLevels");
        textureSource.ShouldContain("OpenGlExternalMemoryImportMipLevels = 1;");
        textureSource.ShouldContain("public void SetOpenGlExternalMemoryImport(nint handle, ulong size, string? label = null, uint mipLevels = 1)");
        textureSource.ShouldContain("OpenGlExternalMemoryImportMipLevels = Math.Max(1u, mipLevels);");

        string storageSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/GLTexture2D.Storage.cs").Replace("\r\n", "\n");
        storageSource.ShouldContain("uint importedLevels = Math.Max(1u, Data.OpenGlExternalMemoryImportMipLevels);");
        storageSource.ShouldContain("levels = importedLevels;");

        string bridgeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeSidecar.cs").Replace("\r\n", "\n");
        bridgeSource.ShouldContain("texture.SetOpenGlExternalMemoryImport(glHandle, memoryRequirements.Size, name, mipLevels: 1);");
    }

    [Test]
    public void ExecutableBuilds_CopyNvidiaVendorRuntimeDllsFromFallbackLocations()
    {
        string targetsSource = ReadWorkspaceFile("Directory.Build.targets").Replace("\r\n", "\n");

        targetsSource.ShouldContain("<NvidiaSdkRuntimeDir Condition=\"'$(NvidiaSdkRuntimeDir)' == '' and Exists('$(MSBuildThisFileDirectory)ThirdParty\\NVIDIA\\SDK\\win-x64\\')\">$(MSBuildThisFileDirectory)ThirdParty\\NVIDIA\\SDK\\win-x64\\</NvidiaSdkRuntimeDir>");
        targetsSource.ShouldContain("<NvidiaSdkRuntimeDir Condition=\"'$(NvidiaSdkRuntimeDir)' == '' and Exists('$(MSBuildThisFileDirectory)XRENGINE\\nvngx_dlss.dll')\">$(MSBuildThisFileDirectory)XRENGINE\\</NvidiaSdkRuntimeDir>");
        targetsSource.ShouldContain("<Content Include=\"$(NvidiaSdkRuntimeDir)nvngx_*.dll\">");
        targetsSource.ShouldContain("<Content Include=\"$(NvidiaSdkRuntimeDir)sl.*.dll\">");
        targetsSource.ShouldContain("<Content Include=\"$(NvidiaSdkRuntimeDir)NvLowLatencyVk.dll\" Condition=\"Exists('$(NvidiaSdkRuntimeDir)NvLowLatencyVk.dll')\">");
    }

    [Test]
    public void MissingNvidiaRuntimeDiagnostic_TellsUsersHowToRetrieveDlls()
    {
        string dlssManagerSource = ReadWorkspaceFile("XRENGINE/Rendering/DLSS/NvidiaDlssManager.cs").Replace("\r\n", "\n");
        string readmeSource = ReadWorkspaceFile("ThirdParty/NVIDIA/SDK/README.md").Replace("\r\n", "\n");
        string installerSource = ReadWorkspaceFile("Tools/Dependencies/Get-StreamlineSdk.ps1").Replace("\r\n", "\n");
        string imguiPropertyEditorSource = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.PropertyEditor.cs").Replace("\r\n", "\n");

        dlssManagerSource.ShouldContain("Download the official NVIDIA Streamline/DLSS SDK");
        dlssManagerSource.ShouldContain("ThirdParty/NVIDIA/SDK/win-x64/");
        dlssManagerSource.ShouldContain("Do not download NVIDIA runtime DLLs from third-party DLL sites");
        dlssManagerSource.ShouldContain("public static bool RequiredRuntimeDllsAvailable");
        dlssManagerSource.ShouldContain("Path.Combine(AppContext.BaseDirectory, libraryName)");
        dlssManagerSource.ShouldContain("TryLoadNativeLibrary(runtimePath, out handle)");
        dlssManagerSource.ShouldContain("public static bool FrameGenerationAvailable");
        dlssManagerSource.ShouldContain("public static string FrameGenerationUnavailableReason");

        readmeSource.ShouldContain("https://github.com/NVIDIA-RTX/Streamline/releases");
        readmeSource.ShouldContain("sl.interposer.dll");
        readmeSource.ShouldContain("sl.common.dll");
        readmeSource.ShouldContain("sl.dlss.dll");
        readmeSource.ShouldContain("nvngx_dlss.dll");
        readmeSource.ShouldContain("sl.dlss_g.dll");
        readmeSource.ShouldContain("sl.reflex.dll");
        readmeSource.ShouldContain("sl.pcl.dll");
        readmeSource.ShouldContain("nvngx_dlssg.dll");
        installerSource.ShouldContain("\"sl.reflex.dll\",");
        installerSource.ShouldContain("\"sl.pcl.dll\",");

        imguiPropertyEditorSource.ShouldContain("DrawInspectorMemberLabel(property, displayName, description, activeOverride);");
        imguiPropertyEditorSource.ShouldContain("NvidiaDlssManager.RequiredRuntimeDllsAvailable");
        imguiPropertyEditorSource.ShouldContain("This DLSS setting will not do anything");
        imguiPropertyEditorSource.ShouldContain("IsNvidiaDlssFrameGenerationMember(member)");
        imguiPropertyEditorSource.ShouldContain("Engine.EffectiveSettings.EnableNvidiaDlssFrameGeneration");
        imguiPropertyEditorSource.ShouldContain("frame generation mode is Off");
        imguiPropertyEditorSource.ShouldContain("NvidiaDlssManager.FrameGenerationAvailable");
        imguiPropertyEditorSource.ShouldContain("NvidiaDlssManager.FrameGenerationUnavailableReason");
        imguiPropertyEditorSource.ShouldContain("ImGui.TextColored(DlssRuntimeWarningColor, \"!\")");
    }

    [Test]
    public void StreamlineDlssBridge_QueriesVulkanRequirementsBeforeCreatingTheSidecarDevice()
    {
        string dlssSource = ReadWorkspaceFile("XRENGINE/Rendering/DLSS/StreamlineNative.cs").Replace("\r\n", "\n");
        dlssSource.ShouldContain("internal static bool TryGetRequiredVulkanRequirements(");
        dlssSource.ShouldContain("StreamlineResult requirementsResult = _getFeatureRequirements!(FeatureDlss, ref requirements);");
        dlssSource.ShouldContain("if (_vulkanInfoInitialized && !MatchesBoundDevice(sidecar))");
        dlssSource.ShouldContain("if (!_vulkanInfoInitialized)");
        dlssSource.ShouldContain("_boundDeviceHandle = sidecar.Device.Handle;");
        dlssSource.ShouldContain("private const string StreamlineProjectId = \"f61b5f80-6a02-4c83-8bb2-96ab8e33d2d7\";");
        dlssSource.ShouldContain("ProjectId = Marshal.StringToHGlobalAnsi(StreamlineProjectId),");
        dlssSource.ShouldContain("TryLoadRuntimeLibrary(StreamlineLibrary, out _libraryHandle)");
        dlssSource.ShouldContain("or on the native probing path");
        dlssSource.ShouldContain("if (!EnsureFrameGenerationRequirements(out failureReason))");
        dlssSource.ShouldContain("internal static int BridgeFailureGeneration");
        dlssSource.ShouldContain("internal static bool HasTerminalBridgeFailure");
        dlssSource.ShouldContain("MarkTerminalBridgeFailure(failureReason);");
        dlssSource.ShouldContain("private static readonly StreamlineStructType FeatureRequirementsStructType");

        string bridgeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeSidecar.cs").Replace("\r\n", "\n");
        bridgeSource.ShouldContain("NvidiaDlssManager.Native.TryGetRequiredVulkanRequirements(");
        bridgeSource.ShouldContain("minApiVersion = DetermineMinimumApiVersionForRequestedFeatureSets(deviceFeatures12, deviceFeatures13);");
        bridgeSource.ShouldContain("ResolveStreamlineQueueConfiguration(");
        bridgeSource.ShouldContain("BuildRequestedVulkanFeatures(");
        bridgeSource.ShouldContain("TryInvokeUserDefinedConversion(");
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

            if (relativePath.StartsWith("XRENGINE/Rendering/", StringComparison.Ordinal))
            {
                string runtimeRenderingPath = "XREngine.Runtime.Rendering/" + relativePath["XRENGINE/".Length..];
                candidate = Path.Combine(dir.FullName, runtimeRenderingPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}
