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
    private const string VulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";

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
                string reason = global::XREngine.RuntimeEngine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(null, hdrRequested: false);
                string bridgeEnvVar = global::XREngine.RuntimeEngine.Rendering.VulkanUpscaleBridgeEnvVar;

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
                string reason = global::XREngine.RuntimeEngine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(null, hdrRequested: true);

                reason.ShouldContain("bridge HDR output is unavailable");
                reason.ShouldContain("cross-adapter imports are blocked");
            });
    }

    [Test]
    public void DefaultRenderPipeline_MapsVendorUpscaleSourcesToExplicitTextures()
    {
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), DefaultRenderPipeline.PostProcessOutputFBOName, DefaultRenderPipeline.PostProcessOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), DefaultRenderPipeline.FxaaFBOName, DefaultRenderPipeline.FxaaOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), DefaultRenderPipeline.SmaaFBOName, DefaultRenderPipeline.SmaaOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), DefaultRenderPipeline.TsrUpscaleFBOName, DefaultRenderPipeline.TsrOutputTextureName);
        AssertSourceTextureMapping(typeof(DefaultRenderPipeline), "UnknownFbo", null);

    }

    [Test]
    public void DefaultRenderPipeline_ThreadsDepthAndMotionResourcesIntoVendorUpscale()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("vendorBlit.SourceTextureName = ResolveVendorUpscaleSourceTextureName(sourceFboName);");
        pipelineSource.ShouldContain("vendorBlit.DepthTextureName = DepthViewTextureName;");
        pipelineSource.ShouldContain("vendorBlit.DepthStencilTextureName = DepthStencilTextureName;");
        pipelineSource.ShouldContain("vendorBlit.MotionTextureName = VelocityTextureName;");
        pipelineSource.ShouldContain("vendorBlit.MotionFrameBufferName = VelocityFBOName;");
    }

    [Test]
    public void VendorUpscaleBridgeDispatch_UsesExposureDepthAndMotionNormalizationContracts()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs").Replace("\r\n", "\n");
        source.ShouldContain("private const float BridgeMotionVectorNormalizationScale = 0.5f;");
        source.ShouldContain("public string AutoExposureTextureName { get; set; } = DefaultRenderPipeline.AutoExposureTextureName;");
        source.ShouldContain("bool hasExposureTexture = sourceExposureFbo is not null;");
        source.ShouldContain("FrameIndex = unchecked((uint)renderer.FrameIndex),");
        source.ShouldContain("ReverseDepth = camera.IsReversedDepth,");
        source.ShouldContain("HasExposureTexture = hasExposureTexture,");
        source.ShouldContain("MotionVectorScaleX = BridgeMotionVectorNormalizationScale,");
        source.ShouldContain("MotionVectorScaleY = BridgeMotionVectorNormalizationScale,");
    }

    [Test]
    public void VulkanUpscaleBridgeSidecar_LocksSharedImageFormatsForMvp()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeSidecar.cs").Replace("\r\n", "\n");
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
        string dlssSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/DLSS/StreamlineNative.cs").Replace("\r\n", "\n");
        dlssSource.ShouldContain("private static StreamlineVulkanInfo CreateVulkanInfo(VulkanUpscaleBridgeVulkanContext context)");
        dlssSource.ShouldContain("Device = context.Device,");
        dlssSource.ShouldContain("Instance = context.Instance,");
        dlssSource.ShouldContain("PhysicalDevice = context.PhysicalDevice,");
        dlssSource.ShouldContain("BufferTypeExposure = 13;");
        dlssSource.ShouldContain("MotionVectorScale = new StreamlineFloat2(parameters.MotionVectorScaleX, parameters.MotionVectorScaleY),");
        dlssSource.ShouldContain("DepthInverted = parameters.ReverseDepth ? StreamlineBoolean.True : StreamlineBoolean.False,");
        dlssSource.ShouldContain("ColorBuffersHdr = parameters.OutputHdr ? StreamlineBoolean.True : StreamlineBoolean.False,");
        dlssSource.ShouldContain("if (parameters.InputWidth == parameters.OutputWidth && parameters.InputHeight == parameters.OutputHeight)\n                        return StreamlineDlssMode.Dlaa;");
        dlssSource.ShouldContain("EDlssQualityMode.UltraQuality => StreamlineDlssMode.MaxQuality,");
        dlssSource.ShouldNotContain("=> StreamlineDlssMode.UltraQuality");
        dlssSource.ShouldContain("LogMessageCallback = RendererNativeCallbackBridge.StreamlineLogCallbackPointer,");
        dlssSource.ShouldContain("StreamlineResourceLifecycle.ValidUntilEvaluate, inputExtent");
        dlssSource.ShouldContain("private static StreamlineResult CallGetOptimalSettings(");
        dlssSource.ShouldContain("DLSS optimal input={settings.OptimalRenderWidth}x{settings.OptimalRenderHeight}");
        dlssSource.ShouldNotContain("StreamlineResult allocateResult = _allocateResources");
        int bridgeSessionIndex = dlssSource.IndexOf("internal sealed class BridgeSession", StringComparison.Ordinal);
        bridgeSessionIndex.ShouldBeGreaterThanOrEqualTo(0);
        int bridgeSessionEndIndex = dlssSource.IndexOf("private static IntPtr ToIntPtr", bridgeSessionIndex, StringComparison.Ordinal);
        bridgeSessionEndIndex.ShouldBeGreaterThan(bridgeSessionIndex);
        string bridgeSessionSource = dlssSource[bridgeSessionIndex..bridgeSessionEndIndex];
        bridgeSessionSource.IndexOf("StreamlineResult setOptionsResult = CallSetOptions(ref viewport, ref options);", StringComparison.Ordinal)
            .ShouldBeLessThan(bridgeSessionSource.IndexOf("StreamlineResult tagResult = CallSetTagForFrame(frameToken, ref viewport, (IntPtr)tags, tagCount, commandBuffer);", StringComparison.Ordinal));
        bridgeSessionSource.IndexOf("StreamlineResult tagResult = CallSetTagForFrame(frameToken, ref viewport, (IntPtr)tags, tagCount, commandBuffer);", StringComparison.Ordinal)
            .ShouldBeLessThan(bridgeSessionSource.IndexOf("StreamlineResult constantsResult = CallSetConstants(ref constants, frameToken, ref viewport);", StringComparison.Ordinal));
        bridgeSessionSource.IndexOf("StreamlineResult constantsResult = CallSetConstants(ref constants, frameToken, ref viewport);", StringComparison.Ordinal)
            .ShouldBeLessThan(bridgeSessionSource.IndexOf("StreamlineResult evaluateResult = CallEvaluateFeature(FeatureDlss, frameToken, (IntPtr)inputs, 1, commandBuffer);", StringComparison.Ordinal));
        bridgeSessionSource.ShouldNotContain("Swapchain");

        string xessSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/XeSS/IntelXessNative.cs").Replace("\r\n", "\n");
        xessSource.ShouldContain("if (!parameters.OutputHdr)");
        xessSource.ShouldContain("XessResult velocityScaleResult = _setVelocityScale(_context, parameters.MotionVectorScaleX, parameters.MotionVectorScaleY);");
        xessSource.ShouldContain("ExposureScaleTexture = parameters.HasExposureTexture ? CreateImageViewInfo(slot.Exposure) : default,");
        xessSource.ShouldContain("ResponsivePixelMaskTexture = default,");
        xessSource.ShouldNotContain("Swapchain");
    }

    [Test]
    public void NativeVulkanDlssSr_QueuesStreamlineIntoTheMainCommandBuffer()
    {
        string vendorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs").Replace("\r\n", "\n");
        vendorSource.ShouldContain("TryResolveNativeDlssDepthTexture");
        vendorSource.ShouldContain("TryResolveNativeDlssMotionTexture");
        vendorSource.ShouldContain("ResolveNativeDlssExposureTexture");
        vendorSource.ShouldContain("ValidateNativeDlssInputSizes(sourceColorTexture, depthTexture, motionTexture, dlssRequested, outputWidth, outputHeight");
        vendorSource.ShouldContain("TryEnsureNativeDlssOutputTexture(outputWidth, outputHeight, outputHdr");
        vendorSource.ShouldContain("TryEnsureNativeDlssSession(renderer, viewport");
        vendorSource.ShouldContain("TryEnsureNativeDlssFrameGenerationSession(renderer, viewport");
        vendorSource.ShouldContain("if (frameGenRequested && !TryEnsureNativeDlssFrameGenerationSession(renderer, viewport");
        vendorSource.ShouldNotContain("if (!dlssRequested && frameGenRequested && !TryEnsureNativeDlssFrameGenerationSession");
        vendorSource.ShouldContain("renderer.TryEnqueueDlssUpscale(");
        vendorSource.ShouldContain("renderer.TryEnqueueDlssUpscale(");
        vendorSource.ShouldContain("renderer.TryEnqueueFrameGeneration(");
        vendorSource.ShouldContain("RememberNativeDlssDispatch(camera, dispatchParameters);");
        vendorSource.ShouldContain("NVIDIA DLSS frame generation failed");
        vendorSource.ShouldContain("renderer.TryDispatchFrameGeneration(");
        vendorSource.ShouldContain("XRTexture hudlessTexture = dlssRequested");
        vendorSource.ShouldContain("in dispatchParameters");
        vendorSource.ShouldContain("depthTexture,");
        vendorSource.ShouldContain("motionTexture,");
        vendorSource.ShouldContain("hudlessTexture,");
        vendorSource.ShouldContain("renderer.FrameIndex + 1UL");
        vendorSource.ShouldContain("NVIDIA DLSS frame generation requires a HUD-less color buffer matching the backbuffer.");
        int dlssUpscaleEnqueueIndex = vendorSource.IndexOf("renderer.TryEnqueueDlssUpscale(", StringComparison.Ordinal);
        int dlssFrameGenerationAfterUpscaleIndex = vendorSource.IndexOf(
            "renderer.TryEnqueueFrameGeneration(",
            dlssUpscaleEnqueueIndex,
            StringComparison.Ordinal);
        dlssUpscaleEnqueueIndex.ShouldBeGreaterThanOrEqualTo(0);
        dlssFrameGenerationAfterUpscaleIndex.ShouldBeGreaterThan(dlssUpscaleEnqueueIndex);

        string vendorBackendSource = ReadWorkspaceFile(VulkanRoot + "Features/Upscaling/VulkanUpscaleBridgeSidecar.VendorBackend.cs").Replace("\r\n", "\n");
        string streamlineImageSource = ReadWorkspaceFile(VulkanRoot + "Features/Upscaling/VulkanStreamlineImage.cs").Replace("\r\n", "\n");
        streamlineImageSource.ShouldContain("internal readonly record struct VulkanStreamlineImage");
        vendorBackendSource.ShouldContain("wrapperLookup.GetOrCreate(texture, generateNow: true) is not IVkImageDescriptorSource source");
        vendorBackendSource.ShouldContain("DeviceMemory memory = source.DescriptorMemory;");
        vendorBackendSource.ShouldContain("internal static bool TryEnqueueDlssUpscale(");
        vendorBackendSource.ShouldContain("commandRuntime.EnqueueFrameOperation(\n            operationQueue,\n            new DlssUpscaleOp(");
        vendorBackendSource.ShouldContain("internal static bool TryEnqueueFrameGeneration(");
        vendorBackendSource.ShouldContain("commandRuntime.EnqueueFrameOperation(\n            operationQueue,\n            new DlssFrameGenerationOp(");

        string nativeRecordingSource = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs").Replace("\r\n", "\n");
        string upscaleRecording = Slice(nativeRecordingSource, "internal void RecordDlssUpscalePayload(CommandBuffer commandBuffer, in DlssUpscalePayload payload)", "internal void RecordDlssFrameGenerationPayload(");
        upscaleRecording.ShouldContain("DlssFrameOp.TransitionImageToGeneral(this, commandBuffer, payload.SourceColor)");
        upscaleRecording.ShouldContain("NvidiaDlssManager.Native.TryRecordNativeVulkanUpscale(");
        upscaleRecording.ShouldContain("DlssFrameOp.ThrowRecordingFailure(\"upscale\", reason)");
        string frameGenerationRecording = Slice(nativeRecordingSource, "internal void RecordDlssFrameGenerationPayload(CommandBuffer commandBuffer, uint imageIndex, in DlssFrameGenerationPayload payload)", "internal void RecordComputeDispatchIndirectOp(");
        frameGenerationRecording.ShouldContain("DlssFrameOp.TransitionImageToGeneral(this, commandBuffer, payload.HudlessColor)");
        frameGenerationRecording.ShouldContain("TryPrepareStreamlineUiImage(commandBuffer, payload.UiColorAndAlpha, out VulkanStreamlineImage ui)");
        frameGenerationRecording.ShouldContain("NvidiaDlssManager.Native.TryRecordNativeVulkanFrameGeneration(");
        frameGenerationRecording.ShouldContain("DlssFrameOp.ThrowRecordingFailure(\"frame generation\", reason)");

        string dlssFrameOpSource = ReadWorkspaceFile(VulkanRoot + "Commands/FrameOps/DlssFrameOp.cs").Replace("\r\n", "\n");
        dlssFrameOpSource.ShouldContain("internal static unsafe VulkanStreamlineImage TransitionImageToGeneral(");
        dlssFrameOpSource.ShouldContain("NewLayout = ImageLayout.General,");
        dlssFrameOpSource.ShouldContain("string message = $\"Requested NVIDIA DLSS {operation} failed during Vulkan command recording: {reason}\";");
        dlssFrameOpSource.ShouldContain("Debug.RenderingError(message);");

        string frameOpSemanticsSource = ReadWorkspaceFile(VulkanRoot + "Commands/FrameOps/VulkanFrameOperationSemantics.cs").Replace("\r\n", "\n");
        string dlssUpscaleOpSource = ReadWorkspaceFile(VulkanRoot + "Commands/FrameOps/DlssUpscaleOp.cs").Replace("\r\n", "\n");
        string dlssFrameGenerationOpSource = ReadWorkspaceFile(VulkanRoot + "Commands/FrameOps/DlssFrameGenerationOp.cs").Replace("\r\n", "\n");
        dlssUpscaleOpSource.ShouldContain("internal sealed record DlssUpscaleOp(");
        dlssFrameGenerationOpSource.ShouldContain("internal sealed record DlssFrameGenerationOp(");
        frameOpSemanticsSource.ShouldContain("op.PassIndex = validatedPassIndex;");
        frameOpSemanticsSource.ShouldNotContain("with { PassIndex = validatedPassIndex }");

        string streamlineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/DLSS/StreamlineNative.cs").Replace("\r\n", "\n");
        streamlineSource.ShouldContain("internal static bool TryCreateNativeVulkanSession(");
        streamlineSource.ShouldContain("internal static bool TryRecordNativeVulkanUpscale(");
        streamlineSource.ShouldContain("internal sealed class NativeVulkanSession : IDisposable");
        streamlineSource.ShouldContain("internal static bool TryCreateNativeFrameGenerationSession(");
        streamlineSource.ShouldContain("internal static bool TryRecordNativeVulkanFrameGeneration(");
        streamlineSource.ShouldContain("internal sealed class NativeFrameGenerationSession : IDisposable");
        streamlineSource.ShouldContain("in VulkanStreamlineDeviceBinding binding,\n                uint viewportId,\n                out NativeVulkanSession? session,");
        streamlineSource.ShouldContain("Device = binding.Device,");
        streamlineSource.ShouldContain("StreamlineResource CreateResource(in VulkanStreamlineImage image");
        streamlineSource.ShouldContain("StreamlineResult evaluateResult = CallEvaluateFeature(FeatureDlss, frameToken, (IntPtr)inputs, 1, commandBufferPtr);");
        streamlineSource.ShouldContain("private const ulong StreamlineSdkVersion = 0x0002000C0000FEDC;");
        streamlineSource.ShouldContain("FeatureDlssG = 1000;");
        streamlineSource.ShouldContain("FeatureReflex = 3;");
        streamlineSource.ShouldContain("FeaturePcl = 4;");
        streamlineSource.ShouldContain("BufferTypeHudLessColor = 2;");
        streamlineSource.ShouldContain("BufferTypeUiColorAndAlpha = 23;");
        streamlineSource.ShouldContain("UiBufferFormat = (uint)uiColorAndAlpha.Format,");
        streamlineSource.ShouldContain("EnableUserInterfaceRecomposition = StreamlineBoolean.True,");
        streamlineSource.ShouldContain("CreateResourceTag(&uiColorAndAlpha, BufferTypeUiColorAndAlpha, StreamlineResourceLifecycle.ValidUntilPresent, outputExtent)");
        streamlineSource.ShouldContain("StreamlineResourceTag* tags = stackalloc StreamlineResourceTag[4];");
        streamlineSource.ShouldContain("slDLSSGSetOptions");
        streamlineSource.ShouldContain("slDLSSGGetState");
        streamlineSource.ShouldContain("TryConfigureFrameGenerationForRecordedFrame(");
        streamlineSource.ShouldContain("_lastConfiguredFrameIndex = parameters.FrameIndex;");
        streamlineSource.ShouldContain("_frameGenerationOptionsFrameIndex == parameters.FrameIndex");
        streamlineSource.ShouldContain("_frameGenerationOptionsViewportId == viewport.Value");
        streamlineSource.ShouldContain("bool frameGenerationRequested = NvidiaDlssManager.IsFrameGenerationRequested;");
        streamlineSource.ShouldContain("if (!frameGenerationRequested)\n                        _frameGenerationHostEnabled = false;");
        streamlineSource.ShouldContain("mode == ENvidiaDlssFrameGenerationMode.Off && _frameGenerationExplicitlyDisabled");
        streamlineSource.ShouldContain("_frameGenerationExplicitlyDisabled = true;");
        streamlineSource.ShouldContain("TryDrainFrameGenerationDisableForPresent(");
        streamlineSource.ShouldContain("_frameGenerationDisableDrainPresentsRemaining = 2;");
        streamlineSource.ShouldContain("if (_frameGenerationExplicitlyDisabled)\n                    {\n                        _frameGenerationHostEnabled = false;");
        int frameGenerationConstantsIndex = streamlineSource.IndexOf(
            "failureReason = DescribeFailure(\"slSetConstants\", constantsResult, in parameters);",
            StringComparison.Ordinal);
        int frameGenerationConfigureIndex = streamlineSource.IndexOf(
            "&& !TryConfigureFrameGenerationForRecordedFrame(",
            frameGenerationConstantsIndex,
            StringComparison.Ordinal);
        frameGenerationConstantsIndex.ShouldBeGreaterThanOrEqualTo(0);
        frameGenerationConfigureIndex.ShouldBeGreaterThan(frameGenerationConstantsIndex);
        streamlineSource.ShouldContain("slReflexSetOptions");
        streamlineSource.ShouldContain("slPCLSetMarker");
        streamlineSource.ShouldContain("EnsureFrameGenerationRequirements");
        streamlineSource.ShouldContain("StreamlineResourceLifecycle.ValidUntilPresent, outputExtent");
        streamlineSource.ShouldContain("TryDisableFrameGeneration");
        streamlineSource.ShouldContain("TryCreateProxySwapchain");
        streamlineSource.ShouldContain("TryAcquireProxyNextImage");
        streamlineSource.ShouldContain("TryQueueProxyPresent");

        string dlssManagerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/DLSS/NvidiaDlssManager.cs").Replace("\r\n", "\n");
        dlssManagerSource.ShouldContain("=> !IsFrameGenerationRequested\n                ? ENvidiaDlssFrameGenerationMode.Off");

        string swapchainSource = ReadWorkspaceFile(VulkanRoot + "Frame/Output/Authority/VulkanDesktopSwapchainService.cs").Replace("\r\n", "\n");
        swapchainSource.ShouldContain("DlssFrameGenerationHdrSurfacePreferences");
        swapchainSource.ShouldContain("DlssFrameGenerationSdrSurfacePreferences");
        swapchainSource.ShouldContain("new(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr)");
        swapchainSource.ShouldContain("NVIDIA DLSS frame generation requires an RGB10 HDR10 or UNORM SDR Vulkan swapchain format");
        swapchainSource.ShouldContain("internal void DisableStreamlineFrameGenerationBeforeMutation(string reason)");
        swapchainSource.ShouldContain("internal void DrainStreamlineFrameGenerationDisableBeforePresent()");
        swapchainSource.ShouldContain("NvidiaDlssManager.Native.TryDisableFrameGeneration");
        swapchainSource.ShouldContain("NvidiaDlssManager.Native.TryDrainFrameGenerationDisableForPresent");
        swapchainSource.ShouldContain("CreateStreamlineUiResources();");
        swapchainSource.ShouldContain("RetireStreamlineUiResources();");

        string presentationProfileSource = ReadWorkspaceFile(VulkanRoot + "Frame/Presentation/VulkanPresentationProfileResolver.cs").Replace("\r\n", "\n");
        string frameGenerationProfile = Slice(presentationProfileSource, "case EVulkanPresentationProfile.FrameGeneration:", "default:");
        frameGenerationProfile.ShouldContain("Vulkan DLSS-G does not\n                // support VSync scheduling");
        frameGenerationProfile.ShouldContain("nativeMode = RequireMode(modes, PresentModeKHR.ImmediateKhr, requested);");
        frameGenerationProfile.ShouldNotContain("PresentModeKHR.FifoKhr");
        frameGenerationProfile.ShouldNotContain("PresentModeKHR.MailboxKhr");

        string streamlineUiSource = ReadWorkspaceFile(VulkanRoot + "Frame/Output/Authority/VulkanDesktopSwapchainService.StreamlineUi.cs").Replace("\r\n", "\n");
        streamlineUiSource.ShouldContain("CreateStreamlineUiResource(index)");
        streamlineUiSource.ShouldContain("_resources.Images.RetireOwnedResources(");
        string streamlineUiPreparationSource = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.StreamlineUiPreparation.cs").Replace("\r\n", "\n");
        streamlineUiPreparationSource.ShouldContain("ClearColorValue transparent = new(0f, 0f, 0f, 0f);");
        streamlineUiPreparationSource.ShouldContain("ImageLayout.TransferDstOptimal");
        streamlineUiPreparationSource.ShouldContain("image = preparedImage with { Layout = ImageLayout.General };");

        string imguiSource = ReadWorkspaceFile(VulkanRoot + "UI/VulkanImGuiOverlayCommandRecorder.cs").Replace("\r\n", "\n");
        string imguiStreamlineUi = Slice(imguiSource, "if (input.Target.HasStreamlineUi)", "input.Target.SwapchainImage,");
        imguiStreamlineUi.ShouldContain("input.Target.StreamlineUiImage,");
        imguiStreamlineUi.ShouldContain("AttachmentLoadOp.Clear,");
        imguiSource.ShouldContain("Color = new ClearColorValue(0f, 0f, 0f, 0f)");

        string dynamicUiSource = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanDynamicUiBatchTextOverlayRecorder.cs").Replace("\r\n", "\n");
        string dynamicUiStreamlineUi = Slice(dynamicUiSource, "if (input.Target.HasStreamlineUi)", "input.Target.SwapchainImage,");
        dynamicUiStreamlineUi.ShouldContain("input.Target.StreamlineUiImage,");
        dynamicUiSource.ShouldContain("LoadOp = AttachmentLoadOp.Load");

        string recoveryPresentationSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/VulkanRenderer.FrameLoop.Recovery.Presentation.cs").Replace("\r\n", "\n");
        string recordingSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/VulkanRenderer.FrameLoop.Recording.cs").Replace("\r\n", "\n");
        string recordingFailuresSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/VulkanRenderer.FrameLoop.Recording.Failures.cs").Replace("\r\n", "\n");
        string presentationSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/VulkanRenderer.FrameLoop.Presentation.cs").Replace("\r\n", "\n");
        recoveryPresentationSource.ShouldContain("presenting rejected Vulkan frame {attempt.FrameNumber} ({rejectionStage})");
        recordingFailuresSource.ShouldContain("IsTransientResourceRetirementRecordingFailure");
        recordingSource.ShouldContain("rejectionStage: \"RecordResourceRetired\"");
        int disableBeforeMutationIndex = presentationSource.IndexOf("DisableStreamlineFrameGenerationBeforeSwapchainMutation(", StringComparison.Ordinal);
        disableBeforeMutationIndex.ShouldBeGreaterThanOrEqualTo(0);
        disableBeforeMutationIndex.ShouldBeLessThan(presentationSource.IndexOf("MarkDlssFrameGenerationPclMarker(", StringComparison.Ordinal));
        presentationSource.ShouldContain("NvidiaDlssManager.Native.StreamlinePclMarker.PresentStart");

        string presentationServicesSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.PresentationServices.cs").Replace("\r\n", "\n");
        presentationServicesSource.ShouldContain("private void DisableStreamlineFrameGenerationBeforeSwapchainMutation(");
        presentationServicesSource.ShouldContain("private void DrainStreamlineFrameGenerationDisableBeforePresent()");
        presentationServicesSource.ShouldContain("NvidiaDlssManager.Native.TryDisableFrameGeneration(");
        presentationServicesSource.ShouldContain("NvidiaDlssManager.Native.TryDrainFrameGenerationDisableForPresent(");

        string secondaryContextSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.Rendering.SecondaryContext.cs").Replace("\r\n", "\n");
        string vulkanSecondaryGate = Slice(secondaryContextSource, "if (templateRenderer?.BackendId == RendererBackendId.Vulkan)", "if (!HasMultipleGpus()");
        vulkanSecondaryGate.ShouldContain("Secondary Vulkan GPU context is unavailable");
        vulkanSecondaryGate.ShouldContain("return;");

        string imageViewSource = ReadWorkspaceFile(VulkanRoot + "Resources/Images/VulkanImageResourceService.cs").Replace("\r\n", "\n");
        imageViewSource.ShouldContain("internal bool IsAvailableForDescriptor(ImageView imageView)");

        string imageBackedTextureSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/Textures/VkImageBackedTexture.cs").Replace("\r\n", "\n");
        imageBackedTextureSource.ShouldContain("!BackendContext.Resources.Images.IsAvailableForDescriptor(view)");
    }

    [Test]
    public void VulkanDescriptorGenerationHandoff_RebuildsRetiredViewsWithoutFirstChanceExceptions()
    {
        string descriptorLifetimeSource = ReadWorkspaceFile(VulkanRoot + "Descriptors/VulkanDescriptorLifetimeAuthority.cs").Replace("\r\n", "\n");
        string applyWrites = Slice(descriptorLifetimeSource, "private bool ApplyDescriptorWrites(", "ValidateAndRecordWritesNoLock(");
        int prevalidateIndex = applyWrites.IndexOf("if (!TryPrevalidateWritesNoLock(", StringComparison.Ordinal);
        int nativeUpdateIndex = applyWrites.IndexOf("device.Api.UpdateDescriptorSets(\n                        device.Device,\n                        descriptorWriteCount,\n                        descriptorWrites,\n                        0,\n                        null);", StringComparison.Ordinal);
        prevalidateIndex.ShouldBeGreaterThanOrEqualTo(0);
        nativeUpdateIndex.ShouldBeGreaterThan(prevalidateIndex);
        applyWrites.ShouldContain("if (prevalidate)\n                        return false;");
        descriptorLifetimeSource.ShouldContain("prevalidate: true,\n            out failureReason);");
        descriptorLifetimeSource.ShouldContain("failureReason = $\"Cannot update retired Vulkan descriptor set {setKey}.\";");
        descriptorLifetimeSource.ShouldNotContain("catch (InvalidOperationException ex) when");

        string materialSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/Materials/VkMaterial.cs").Replace("\r\n", "\n");
        materialSource.ShouldContain("allowRetirementRetry: true");
        materialSource.ShouldContain("BackendContext.Resources.DescriptorLifetime.TryUpdateDescriptorSets(");
        materialSource.ShouldContain("descriptorUpdateFailure.Contains(\"retired Vulkan resource\", StringComparison.Ordinal)");
        materialSource.ShouldContain("allowRetirementRetry: false");

        string imageBackedTextureSource = SourceContractWorkspace.ReadPartialType(VulkanRoot + "BackendObjects/Textures/VkImageBackedTexture.cs");
        imageBackedTextureSource.ShouldContain("RefreshPrimaryDescriptorViewForUseNoLock");
        imageBackedTextureSource.ShouldContain("&& BackendContext.Resources.Images.IsAvailableForDescriptor(_view)");
        imageBackedTextureSource.ShouldContain("ForgetCurrentViews(removeActiveCacheEntry: true);");
        imageBackedTextureSource.ShouldContain("PublishDescriptorViewRefreshNoLock");
        imageBackedTextureSource.ShouldContain("MarkDescriptorPublished();");
        imageBackedTextureSource.ShouldContain("!BackendContext.Resources.Images.IsAvailableForDescriptor(cached)");
        imageBackedTextureSource.ShouldContain("ReleaseCurrentImageBeforeBorrowingPhysicalGroup(group);");
        imageBackedTextureSource.ShouldContain("!ReferenceEquals(_physicalGroup, targetGroup)");
        imageBackedTextureSource.ShouldNotContain("RetireDedicatedImageBeforeBorrowingPhysicalGroup");
        imageBackedTextureSource.ShouldContain("BackendContext.Resources.Images.IsAvailableForDescriptor(reusableView)");

        string textureViewSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/Textures/VkTextureView.cs").Replace("\r\n", "\n");
        textureViewSource.ShouldContain("BackendContext.Resources.Images.IsLiveBackedByLiveImage(view) &&\n                BackendContext.Resources.Images.IsAvailableForDescriptor(view);");
        textureViewSource.ShouldContain("MarkDescriptorDirty();\n                    MarkDescriptorPublished();");

        string imageResourceSource = ReadWorkspaceFile(VulkanRoot + "Resources/Images/VulkanImageResourceService.cs").Replace("\r\n", "\n");
        imageResourceSource.ShouldContain("record.PublishedGeneration == existing.Generation &&\n                        IsAvailableForDescriptor(existing.View))");

        string imguiSource = ReadWorkspaceFile(VulkanRoot + "UI/VulkanImGuiTextureRegistryService.cs").Replace("\r\n", "\n");
        imguiSource.ShouldContain("registration.DescriptorGeneration == descriptorGeneration");
        imguiSource.ShouldContain("DescriptorSet replacementSet = AllocateImGuiDescriptorSet(descriptorView, descriptorSampler, descriptorLayout);");
        imguiSource.ShouldContain("ImGuiTextureOutputResources.Retire(_resourcesState, descriptorSet);");
        imguiSource.ShouldContain("_resources.Images.IsAvailableForDescriptor(descriptorView)");
        imguiSource.ShouldNotContain("TryUpdateImGuiDescriptorSet(");

        string trackingBatchSource = ReadWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Tracking/VulkanRenderer.CommandBufferTrackingBatch.cs").Replace("\r\n", "\n");
        trackingBatchSource.ShouldContain("out string trackingFailure");

        string recordingPreparationSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/VulkanFrameLoop.PrimaryRecordingPreparation.cs").Replace("\r\n", "\n");
        recordingPreparationSource.ShouldContain("int recordingAttemptLimit = attempt.InteractiveResize ? 1 : 2;");
        recordingPreparationSource.ShouldContain("for (int replanAttempt = 0; replanAttempt < recordingAttemptLimit; replanAttempt++)");

        string recordingSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/VulkanRenderer.FrameLoop.Recording.cs").Replace("\r\n", "\n");
        recordingSource.ShouldContain("catch (InvalidOperationException ex)\n                        when (VulkanRecordingFailurePolicy.IsTransientResourceRetirement(ex))");
        recordingSource.ShouldContain("\"command buffer resource generation retired during recording\"");
        recordingSource.ShouldContain("rejectionStage: \"RecordResourceRetired\"");
    }

    [Test]
    public void DefaultRenderPipelines_RequestVendorRenderScaleEveryFrame()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("if (!IsRenderingExternalSwapchainTarget() && TryResolveVendorInternalResolutionScale(out float vendorScale))\n            return vendorScale;");
        pipelineSource.ShouldContain("if (mode == EAntiAliasingMode.Dlaa)\n            return 1.0f;");
        pipelineSource.ShouldContain("RequestedInternalResolution = 1.0f;");
        pipelineSource.ShouldContain("RequestedInternalResolution = vendorScale;");
        pipelineSource.ShouldContain("VendorUpscaleRuntime.GetDlssRecommendedRenderScale(RuntimeEngine.Rendering.Settings)");
        pipelineSource.ShouldContain("VendorUpscaleRuntime.GetXessRecommendedRenderScale(RuntimeEngine.Rendering.Settings)");

        string advancedPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs").Replace("\r\n", "\n");
        advancedPipelineSource.ShouldContain("if (!IsRenderingExternalSwapchainTarget() && TryResolveVendorInternalResolutionScale(out float vendorScale))\n            return vendorScale;");
        advancedPipelineSource.ShouldContain("if (mode == EAntiAliasingMode.Dlaa)\n            return 1.0f;");
        advancedPipelineSource.ShouldContain("RequestedInternalResolution = 1.0f;");
        advancedPipelineSource.ShouldContain("RequestedInternalResolution = vendorScale;");
        advancedPipelineSource.ShouldContain("VendorUpscaleRuntime.GetDlssRecommendedRenderScale(RuntimeEngine.Rendering.Settings)");
        advancedPipelineSource.ShouldContain("VendorUpscaleRuntime.GetXessRecommendedRenderScale(RuntimeEngine.Rendering.Settings)");

        string dlssManagerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/DLSS/NvidiaDlssManager.cs").Replace("\r\n", "\n");
        dlssManagerSource.ShouldContain("internal static float GetRecommendedRenderScale(object? settings = null)");
        dlssManagerSource.ShouldContain("if (RuntimeEngine.EffectiveSettings.AntiAliasingMode == EAntiAliasingMode.Dlaa)\n                return MaxScale;");
        dlssManagerSource.ShouldContain("if (!RuntimeEngine.EffectiveSettings.EnableNvidiaDlss || !_cachedIsSupported)");

        string xessManagerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/XeSS/IntelXessManager.cs").Replace("\r\n", "\n");
        xessManagerSource.ShouldContain("internal static float GetRecommendedRenderScale(object? settings = null)");
    }

    [Test]
    public void VendorUpscaleRuntime_SkipsPostAaUpscaleAndHonorsExplicitSourceTexture()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("private static bool RuntimeEnableVendorUpscale");
        pipelineSource.ShouldContain("|| ResolveAntiAliasingMode() == EAntiAliasingMode.Dlaa");
        pipelineSource.ShouldContain("private static bool RuntimeSuppressOwnAntiAliasing\n        => !IsRenderingExternalSwapchainTarget()\n        && (RuntimeEngine.EffectiveSettings.EnableNvidiaDlss\n            || RuntimeEngine.EffectiveSettings.EnableIntelXess\n            || ResolveAntiAliasingMode() == EAntiAliasingMode.Dlaa);");
        pipelineSource.ShouldContain("private static bool RuntimeEnableFxaa\n        => !RuntimeSuppressOwnAntiAliasing && ResolveAntiAliasingMode() == EAntiAliasingMode.Fxaa;");
        pipelineSource.ShouldContain("private static bool RuntimeEnableSmaa\n        => !RuntimeSuppressOwnAntiAliasing && ResolveAntiAliasingMode() == EAntiAliasingMode.Smaa;");
        pipelineSource.ShouldContain("private static bool RuntimeNeedsTsrUpscale\n        => !RuntimeSuppressOwnAntiAliasing\n        && !DisableHistoryBasedVrEffects()\n        && ResolveAntiAliasingMode() == EAntiAliasingMode.Tsr;");

        string advancedPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs").Replace("\r\n", "\n");
        advancedPipelineSource.ShouldContain("private static bool RuntimeEnableVendorUpscale");
        advancedPipelineSource.ShouldContain("|| ResolveAntiAliasingMode() == EAntiAliasingMode.Dlaa");
        advancedPipelineSource.ShouldContain("private static bool RuntimeSuppressOwnAntiAliasing\n        => !IsRenderingExternalSwapchainTarget()\n        && (RuntimeEngine.EffectiveSettings.EnableNvidiaDlss\n            || RuntimeEngine.EffectiveSettings.EnableIntelXess\n            || ResolveAntiAliasingMode() == EAntiAliasingMode.Dlaa);");
        advancedPipelineSource.ShouldContain("private static bool RuntimeEnableFxaa\n        => !RuntimeSuppressOwnAntiAliasing && ResolveAntiAliasingMode() == EAntiAliasingMode.Fxaa;");
        advancedPipelineSource.ShouldContain("private static bool RuntimeEnableSmaa\n        => !RuntimeSuppressOwnAntiAliasing && ResolveAntiAliasingMode() == EAntiAliasingMode.Smaa;");
        advancedPipelineSource.ShouldContain("private static bool RuntimeNeedsTsrUpscale\n        => !RuntimeSuppressOwnAntiAliasing\n        && !DisableHistoryBasedVrEffects()\n        && ResolveAntiAliasingMode() == EAntiAliasingMode.Tsr;");

        string vendorUpscaleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs").Replace("\r\n", "\n");
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
        string probeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeProbe.cs").Replace("\r\n", "\n");
        probeSource.ShouldContain("\"VK_KHR_external_memory\"");
        probeSource.ShouldContain("\"VK_KHR_external_semaphore\"");
        probeSource.ShouldContain("\"VK_KHR_external_memory_win32\"");
        probeSource.ShouldContain("\"VK_KHR_external_semaphore_win32\"");
        probeSource.ShouldContain("ProbeFailureReason = selected.SupportsBridgeImport");

        string bridgeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridge.cs").Replace("\r\n", "\n");
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

        string sidecarSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeSidecar.cs").Replace("\r\n", "\n");
        sidecarSource.ShouldContain("public VulkanUpscaleBridgeFrameSlot[] RecreateFrameSlots(IOpenGlVendorUpscaleBackendCapability renderer, VulkanUpscaleBridgeFrameResources frameResources, string viewportTag)");
        sidecarSource.ShouldContain("ResetVendorSessionsForFrameResourceRecreate();");
        sidecarSource.ShouldContain("_dlssSession?.ResetResources();");

        string dlssSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/DLSS/StreamlineNative.cs").Replace("\r\n", "\n");
        dlssSource.ShouldContain("public void ResetResources()");
        dlssSource.ShouldContain("FreeAllocatedResources();");

        string bridgeEnvSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.Rendering.VulkanUpscaleBridge.cs").Replace("\r\n", "\n");
        bridgeEnvSource.ShouldContain("public static bool VulkanUpscaleBridgeRequested => IsEnvFlagEnabled(VulkanUpscaleBridgeEnvVar, defaultValue: true);");
        bridgeEnvSource.ShouldContain("public static bool VulkanUpscaleBridgeHdrSupported => true;");
        bridgeEnvSource.ShouldContain("EVulkanUpscaleBridgeSurfaceSet.Exposure;");
        bridgeEnvSource.ShouldContain("disabled the OpenGL->Vulkan upscale bridge");

        string engineRenderingSource = ReadWorkspaceFile("XREngine.Runtime.Host/Engine/Subclasses/Rendering/EngineRenderingSettingsApplication.Preferences.cs").Replace("\r\n", "\n");
        engineRenderingSource.ShouldContain("InvalidateAllVulkanUpscaleBridges(\"anti-aliasing settings changed\");");
        engineRenderingSource.ShouldContain("NotifyVulkanUpscaleBridgeVendorSelectionChanged(\"NVIDIA DLSS preference changed\");");
        engineRenderingSource.ShouldContain("NotifyVulkanUpscaleBridgeVendorSelectionChanged(\"Intel XeSS preference changed\");");
        engineRenderingSource.ShouldContain("RefreshWindowsAfterVendorUpscalePreferenceChanged();");
        engineRenderingSource.ShouldContain("window.InvalidateScenePanelResources();");
        engineRenderingSource.ShouldContain("window.RequestRenderStateRecheck(resetCircuitBreaker: true);");
        engineRenderingSource.ShouldContain("FrameGenerationRequested={8} FrameGenerationAvailable={9} FrameGenerationUnavailableReason={10}");
        engineRenderingSource.ShouldContain("NvidiaDlssFrameGenerationMode is Off");
        engineRenderingSource.ShouldContain("Frame generation is requested, but unavailable");

        string vendorUpscaleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs").Replace("\r\n", "\n");
        vendorUpscaleSource.ShouldContain("bridgeRenderer.TryGetBackendCapability<IOpenGlVendorUpscaleBackendCapability>(out var openGlBridge) &&");
        vendorUpscaleSource.ShouldContain("if (TryRunBridge(openGlBridge, viewport, sourceFrameBuffer, resolvedColorTexture, out string bridgeFailure))");
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
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/GLTexture2D.SparseStreaming.Async.cs").Replace("\r\n", "\n");

        source.ShouldContain("bool hasPreviousCommit = previousCommittedBaseMipLevel != int.MaxValue;");
        source.ShouldContain("bool isDemotion = hasPreviousCommit && committedBaseMipLevel > previousCommittedBaseMipLevel;");
        source.ShouldContain("if (!hasPreviousCommit)");
        source.ShouldContain("if (isDemotion)");

        string hostSource = ReadWorkspaceFile("XREngine.Runtime.Host/RenderingHost/Engine.RuntimeRenderingHostServices.cs").Replace("\r\n", "\n");
        hostSource.ShouldContain("public SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(");
        hostSource.ShouldContain("capability.FinalizeSparseTextureStreamingTransition(texture, request, transitionResult)");

        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs").Replace("\r\n", "\n");
        managerSource.ShouldContain("private int _sparseFinalizeScheduled;");
        managerSource.ShouldContain("if (!RuntimeRenderingHostServices.FrameTiming.IsRenderThread)");
        managerSource.ShouldContain("TextureStreaming.FinalizeSparseTransitions");
    }

    [Test]
    public void WindowCloseRequests_AreDeferredToTheFrameBoundary()
    {
        string windowSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs").Replace("\r\n", "\n");
        string editorSource = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.ImGui.cs").Replace("\r\n", "\n");
        string engineLifecycleSource = ReadWorkspaceFile("XREngine.Runtime.Host/Engine/Engine.Lifecycle.cs").Replace("\r\n", "\n");

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
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Bootstrap/OpenGLRenderer.cs").Replace("\r\n", "\n");

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

        string storageSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/GLTexture2D.Storage.cs").Replace("\r\n", "\n");
        storageSource.ShouldContain("uint importedLevels = Math.Max(1u, Data.OpenGlExternalMemoryImportMipLevels);");
        storageSource.ShouldContain("levels = importedLevels;");

        string bridgeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeSidecar.cs").Replace("\r\n", "\n");
        bridgeSource.ShouldContain("texture.SetOpenGlExternalMemoryImport(glHandle, memoryRequirements.Size, name, mipLevels: 1);");
    }

    [Test]
    public void ExecutableBuilds_CopyNvidiaVendorRuntimeDllsFromFallbackLocations()
    {
        string targetsSource = ReadWorkspaceFile("Directory.Build.targets").Replace("\r\n", "\n");

        targetsSource.ShouldContain("<NvidiaSdkRuntimeDir Condition=\"'$(NvidiaSdkRuntimeDir)' == '' and Exists('$(MSBuildThisFileDirectory)ThirdParty\\NVIDIA\\SDK\\win-x64\\')\">$(MSBuildThisFileDirectory)ThirdParty\\NVIDIA\\SDK\\win-x64\\</NvidiaSdkRuntimeDir>");
        targetsSource.ShouldNotContain("$(MSBuildThisFileDirectory)XRENGINE\\");
        targetsSource.ShouldContain("and '$(NvidiaSdkRuntimeDir)' != ''\">");
        targetsSource.ShouldContain("<Content Include=\"$(NvidiaSdkRuntimeDir)nvngx_*.dll\">");
        targetsSource.ShouldContain("<Content Include=\"$(NvidiaSdkRuntimeDir)sl.*.dll\">");
        targetsSource.ShouldContain("<Content Include=\"$(NvidiaSdkRuntimeDir)NvLowLatencyVk.dll\" Condition=\"Exists('$(NvidiaSdkRuntimeDir)NvLowLatencyVk.dll')\">");
    }

    [Test]
    public void MissingNvidiaRuntimeDiagnostic_TellsUsersHowToRetrieveDlls()
    {
        string dlssManagerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/DLSS/NvidiaDlssManager.cs").Replace("\r\n", "\n");
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

        installerSource.ShouldContain("\"sl.reflex.dll\",");
        installerSource.ShouldContain("\"sl.pcl.dll\",");

        imguiPropertyEditorSource.ShouldContain("DrawInspectorMemberLabel(property, displayName, description, activeOverride);");
        imguiPropertyEditorSource.ShouldContain("if (!VendorUpscaleRuntime.AreDlssRuntimeLibrariesAvailable)");
        imguiPropertyEditorSource.ShouldContain("This DLSS setting will not do anything");
        imguiPropertyEditorSource.ShouldContain("IsNvidiaDlssFrameGenerationMember(member)");
        imguiPropertyEditorSource.ShouldContain("Engine.EffectiveSettings.EnableNvidiaDlssFrameGeneration");
        imguiPropertyEditorSource.ShouldContain("frame generation mode is Off");
        imguiPropertyEditorSource.ShouldContain("!VendorUpscaleRuntime.IsDlssFrameGenerationSupported");
        imguiPropertyEditorSource.ShouldContain("VendorUpscaleRuntime.DlssFrameGenerationUnavailableReason");

        string vendorUpscaleServiceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/VulkanVendorUpscaleService.cs").Replace("\r\n", "\n");
        vendorUpscaleServiceSource.ShouldContain("public bool AreDlssRuntimeLibrariesAvailable => NvidiaDlssManager.RequiredRuntimeDllsAvailable;");
        vendorUpscaleServiceSource.ShouldContain("public bool IsDlssFrameGenerationSupported => NvidiaDlssManager.FrameGenerationAvailable;");
        vendorUpscaleServiceSource.ShouldContain("public string? DlssFrameGenerationUnavailableReason => NvidiaDlssManager.FrameGenerationUnavailableReason;");
        imguiPropertyEditorSource.ShouldContain("ImGui.TextColored(DlssRuntimeWarningColor, \"!\")");
    }

    [Test]
    public void DlssRuntimeToggle_WaitsForMatchingResourcesAndReportsPresentedFrames()
    {
        string vendorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs").Replace("\r\n", "\n");
        vendorSource.ShouldContain("bool vendorResourcesReady = AreRequestedVendorResourcesActive();");
        vendorSource.ShouldContain("Keeping the active fallback presentation path until requested vendor resources commit");
        vendorSource.ShouldContain("active is not null && (pending is null || active.Key == pending.Key)");

        string streamlineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/DLSS/StreamlineNative.cs").Replace("\r\n", "\n");
        streamlineSource.ShouldContain("options.NumFramesToGenerate = ResolveSupportedFramesToGenerate(requestedFramesToGenerate);");
        streamlineSource.ShouldContain("return supportedMaximum == 0");
        streamlineSource.ShouldContain("Math.Min(requested, supportedMaximum)");
        streamlineSource.ShouldContain("RecordFrameGenerationState(viewport.Value, in state);");
        streamlineSource.ShouldContain("DLSS-G requested {0} generated frames, but the runtime supports at most {1}");

        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/DLSS/NvidiaDlssManager.cs").Replace("\r\n", "\n");
        managerSource.ShouldContain("public static uint FrameGenerationFramesActuallyPresented");
        managerSource.ShouldContain("public static ulong FrameGenerationFramesActuallyPresentedTotal");

        string overlaySource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs").Replace("\r\n", "\n");
        overlaySource.ShouldContain("builder.Append(\"\\ndlss-g: presented \"");
        overlaySource.ShouldContain("VendorUpscaleRuntime.DlssFrameGenerationFramesActuallyPresentedTotal");

        string vendorUpscaleServiceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/VulkanVendorUpscaleService.cs").Replace("\r\n", "\n");
        vendorUpscaleServiceSource.ShouldContain("public uint DlssFrameGenerationMaximumFramesToGenerate => NvidiaDlssManager.FrameGenerationMaximumFramesToGenerate;");
        vendorUpscaleServiceSource.ShouldContain("public ulong DlssFrameGenerationFramesActuallyPresentedTotal => NvidiaDlssManager.FrameGenerationFramesActuallyPresentedTotal;");

        string propertyEditorSource = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.PropertyEditor.cs").Replace("\r\n", "\n");
        propertyEditorSource.ShouldContain("uint maximumGeneratedFrames = VendorUpscaleRuntime.DlssFrameGenerationMaximumFramesToGenerate;");
        propertyEditorSource.ShouldContain("The engine is using {maximumGeneratedFrames}");
    }

    [Test]
    public void StreamlineDlssBridge_QueriesVulkanRequirementsBeforeCreatingTheSidecarDevice()
    {
        string dlssSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/DLSS/StreamlineNative.cs").Replace("\r\n", "\n");
        dlssSource.ShouldContain("internal static bool TryGetRequiredVulkanRequirements(");
        dlssSource.ShouldContain("foreach (uint feature in requestedFeatures)");
        dlssSource.ShouldContain("StreamlineResult requirementsResult = CallGetFeatureRequirements(feature, ref requirements);");
        dlssSource.ShouldContain("? [FeatureDlss, FeatureDlssG, FeatureReflex, FeaturePcl]");
        dlssSource.ShouldContain("if (_vulkanInfoInitialized && !MatchesBoundDevice(context))");
        dlssSource.ShouldContain("if (!_vulkanInfoInitialized)");
        dlssSource.ShouldContain("_boundDeviceHandle = context.Device.Handle;");
        dlssSource.ShouldContain("private const string StreamlineProjectId = \"f61b5f80-6a02-4c83-8bb2-96ab8e33d2d7\";");
        dlssSource.ShouldContain("ProjectId = Marshal.StringToHGlobalAnsi(StreamlineProjectId),");
        dlssSource.ShouldContain("TryLoadRuntimeLibrary(StreamlineLibrary, out _libraryHandle)");
        dlssSource.ShouldContain("or on the native probing path");
        dlssSource.ShouldContain("if (!EnsureFrameGenerationRequirements(out failureReason))");
        dlssSource.ShouldContain("internal static int BridgeFailureGeneration");
        dlssSource.ShouldContain("internal static bool HasTerminalBridgeFailure");
        dlssSource.ShouldContain("MarkTerminalBridgeFailure(failureReason);");
        dlssSource.ShouldContain("private static readonly StreamlineStructType FeatureRequirementsStructType");

        string bridgeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeSidecar.cs").Replace("\r\n", "\n");
        bridgeSource.ShouldContain("NvidiaDlssManager.Native.TryGetRequiredVulkanRequirements(");
        bridgeSource.ShouldContain("minApiVersion = DetermineMinimumApiVersionForRequestedFeatureSets(deviceFeatures12, deviceFeatures13);");
        bridgeSource.ShouldContain("ResolveStreamlineQueueConfiguration(");
        bridgeSource.ShouldContain("BuildRequestedVulkanFeatures(");
        bridgeSource.ShouldContain("TryInvokeUserDefinedConversion(");

        string provisioningSource = ReadWorkspaceFile(VulkanRoot + "Features/Upscaling/VulkanUpscaleBridgeSidecar.Provisioning.cs").Replace("\r\n", "\n");
        provisioningSource.ShouldContain("internal static VulkanStreamlineProvisioningSnapshot PrepareStreamlineVulkanRequirements(");
        string outputProvisioningSource = ReadWorkspaceFile(VulkanRoot + "Features/Upscaling/VulkanOutputRuntime.StreamlineProvisioning.cs").Replace("\r\n", "\n");
        outputProvisioningSource.ShouldContain("=> PublishStreamlineProvisioning(VulkanUpscaleBridgeSidecar.PrepareStreamlineVulkanRequirements(");
        outputProvisioningSource.ShouldContain("_streamlineRequiredDeviceExtensions = snapshot.RequiredDeviceExtensions;");

        string logicalDeviceSource = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs").Replace("\r\n", "\n");
        logicalDeviceSource.ShouldContain("AppendRequiredQueues(");
        logicalDeviceSource.ShouldContain("FindOpticalFlowQueueFamily(");
        logicalDeviceSource.ShouldContain("_streamlineRequiredDeviceExtensions");
        logicalDeviceSource.ShouldContain("PopulateStreamlineRequiredFeatures(");
        logicalDeviceSource.ShouldContain("PhysicalDeviceOpticalFlowFeaturesNV opticalFlowFeatureEnable");

        string lifecycleSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs").Replace("\r\n", "\n");
        int prepareRequirementsIndex = lifecycleSource.IndexOf("_outputRuntime.PrepareStreamlineVulkanRequirements(", StringComparison.Ordinal);
        int consumeExtensionsIndex = lifecycleSource.IndexOf("_outputRuntime._streamlineRequiredDeviceExtensions,", StringComparison.Ordinal);
        prepareRequirementsIndex.ShouldBeGreaterThanOrEqualTo(0);
        consumeExtensionsIndex.ShouldBeGreaterThan(prepareRequirementsIndex);
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
        Type renderingType = typeof(global::XREngine.RuntimeEngine).GetNestedType("Rendering", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not resolve RuntimeEngine.Rendering nested type.");
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
        => SourceContractWorkspace.ReadFile(relativePath);

    /// <summary>
    /// Returns the source text from the start marker up to the next end marker.
    /// The test fails when a marker is missing.
    /// </summary>
    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Missing start marker: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Missing end marker: {endMarker}");
        return source[start..end];
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
