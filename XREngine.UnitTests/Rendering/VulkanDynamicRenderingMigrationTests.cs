using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanDynamicRenderingMigrationTests
{
    [Test]
    public void RenderTargetMode_HasEnvironmentOverrideAndVisibleUnsupportedDynamicFailure()
    {
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderTargetMode.cs");
        string environmentSource = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");

        modeSource.ShouldContain("XREngineEnvironmentVariables.VkRenderTargetMode");
        environmentSource.ShouldContain(XREngineEnvironmentVariables.VkRenderTargetMode);
        modeSource.ShouldContain("VulkanRenderTargetMode.Auto");
        modeSource.ShouldContain("VulkanRenderTargetMode.DynamicRendering");
        modeSource.ShouldContain("VulkanRenderTargetMode.LegacyRenderPass");
        modeSource.ShouldContain("dynamic rendering was explicitly requested");
        logicalDeviceSource.ShouldContain("ResolveRenderTargetMode();");
        logicalDeviceSource.ShouldContain("[Vulkan] Render target mode:");
    }

    [Test]
    public void DynamicCommandRecording_UsesDynamicRenderingAndKeepsLegacyCallsModeGated()
    {
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string extensions = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanExtensions.cs");
        string frameBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Framebuffers/VulkanRenderer.SwapchainFramebuffers.cs");
        string renderPasses = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderer.RenderPasses.cs");

        commandBuffers.ShouldContain("UseDynamicRenderingRenderTargets &&");
        commandBuffers.ShouldContain("CmdBeginDynamicRendering(commandBuffer, &renderingInfo);");
        commandBuffers.ShouldContain("CmdEndDynamicRendering(commandBuffer);");
        extensions.ShouldContain("Api!.CmdBeginRendering(commandBuffer, renderingInfo);");
        extensions.ShouldContain("_khrDynamicRendering.CmdBeginRendering(commandBuffer, renderingInfo);");
        commandBuffers.ShouldContain("TransitionFboAttachmentsForDynamicRendering");
        commandBuffers.ShouldContain("CmdBeginRenderPassTracked(");
        commandBuffers.ShouldContain("&fboPassInfo,");
        commandBuffers.ShouldContain("SubpassContents.Inline");
        frameBuffers.ShouldContain("if (UseDynamicRenderingRenderTargets)");
        frameBuffers.ShouldContain("swapChainFramebuffers = new Framebuffer[swapChainImageViews.Length];");
        renderPasses.ShouldContain("if (UseDynamicRenderingRenderTargets)");
        renderPasses.ShouldContain("_renderPass = default;");
    }

    [Test]
    public void DynamicCommandRecording_UsesSharedScopeAndAttachmentPlans()
    {
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderTargetMode.cs");

        modeSource.ShouldContain("internal readonly struct DynamicRenderingAttachmentPlan");
        modeSource.ShouldContain("ImageView ResolveImageView");
        modeSource.ShouldContain("ResolveModeFlags ResolveMode");
        modeSource.ShouldContain("internal readonly ref struct DynamicRenderingScopePlan");
        modeSource.ShouldContain("ReadOnlySpan<DynamicRenderingAttachmentPlan> ColorAttachments");
        modeSource.ShouldContain("SampleCountFlags SampleCount");

        commandBuffers.ShouldContain("void BeginDynamicRenderingScope(in DynamicRenderingScopePlan plan, bool secondaryContents)");
        commandBuffers.ShouldContain("colorPlans[i].ToRenderingAttachmentInfo()");
        commandBuffers.ShouldContain("Span<DynamicRenderingAttachmentPlan> colorAttachmentPlans = stackalloc DynamicRenderingAttachmentPlan[1];");
        commandBuffers.ShouldContain("colorAttachmentPlans[..colorAttachmentCount]");
        commandBuffers.ShouldContain("ResolveDynamicRenderingSampleCount(fboSignature)");
        commandBuffers.ShouldContain("BeginDynamicRenderingScope(in scopePlan, secondaryContents)");
        commandBuffers.ShouldContain("BeginDynamicRenderingScope(in scopePlan, secondaryContents: true)");
        commandBuffers.ShouldContain("TryResolveAttachmentImage(");
    }

    [Test]
    public void DynamicRenderingFormatIdentity_UsesAllocationFreeInlineStorage()
    {
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderTargetMode.cs");

        modeSource.ShouldContain("[InlineArray(MaxColorAttachmentCount)]");
        modeSource.ShouldContain("private readonly ColorFormatStorage _colorFormats;");
        modeSource.ShouldContain("private readonly byte _colorAttachmentCount;");
        modeSource.ShouldContain("stackalloc Format[colorCount]");
        modeSource.ShouldNotContain("ReadOnlySpan<Format>.ToArray()");
        modeSource.ShouldNotContain("colorFormats.ToArray()");
        modeSource.ShouldNotContain("new Format[colorCount]");
        modeSource.ShouldNotContain("Format[]? _colorFormats");
    }

    [Test]
    public void DynamicCommandRecording_ClearsMultiviewFramebuffersAsSingleLayerRenderPasses()
    {
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderTargetMode.cs");
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");

        modeSource.ShouldContain("viewMask == 0u ? Math.Max(framebufferLayers, 1u) : 1u");
        commandBuffers.ShouldContain("clearTargetFrameBuffer?.MultiviewViewMask != 0u");
        commandBuffers.ShouldContain("activeDynamicRenderingFormats.ViewMask");
        commandBuffers.ShouldContain("ResolveClearRectLayerCount(op.Target, clearTargetFrameBuffer, activeRenderLayerCount, activeRenderViewMask)");
        commandBuffers.ShouldContain("if (activeRenderViewMask != 0u || clearTargetFrameBuffer?.MultiviewViewMask != 0u)");
        commandBuffers.ShouldContain("IsStereoCompatibleClearTarget(target, clearTargetFrameBuffer)");
        commandBuffers.ShouldContain("activeRenderLayerCount > 1u && RuntimeEngine.Rendering.State.IsStereoPass");
        commandBuffers.ShouldContain("ClearRect clearRect = new()");
        frameBuffer.ShouldContain("RuntimeEngine.Rendering.State.IsStereoPass");
        frameBuffer.ShouldContain("IsTextureArrayAttachment(texture)");
        frameBuffer.ShouldContain("TryGetTextureArrayMultiviewParameters(texture");
        frameBuffer.ShouldContain("XRTexture2DArrayView textureArrayView => textureArrayView.ViewedTexture.OVRMultiViewParameters");
        frameBuffer.ShouldContain("IsStereoCompatibleTextureArrayAttachment(texture, layerCount)");
        frameBuffer.ShouldContain("descriptor.StereoCompatible && descriptorLayers >= 2u");
        frameBuffer.ShouldContain("BuildMultiviewViewMask(0, 2u, layerCount)");
    }

    [Test]
    public void VulkanFramebuffer_WriteBindingTracksDrawFramebufferState()
    {
        string glFrameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Framebuffers/GLFrameBuffer.cs");
        string vkFrameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string bindFboCommand = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/State/VPRC_BindFBO.cs");

        glFrameBuffer.ShouldContain("Data.BindForWriteRequested += BindForWriting;");
        glFrameBuffer.ShouldContain("Data.BindForWriteRequested -= BindForWriting;");
        glFrameBuffer.ShouldContain("Data.UnbindFromWriteRequested += UnbindFromWriting;");
        glFrameBuffer.ShouldContain("Data.UnbindFromWriteRequested -= UnbindFromWriting;");

        vkFrameBuffer.ShouldContain("Data.BindForWriteRequested += BindForWriting;");
        vkFrameBuffer.ShouldContain("Data.BindForWriteRequested -= BindForWriting;");
        vkFrameBuffer.ShouldContain("Data.UnbindFromWriteRequested += UnbindFromWriting;");
        vkFrameBuffer.ShouldContain("Data.UnbindFromWriteRequested -= UnbindFromWriting;");
        vkFrameBuffer.ShouldContain("Renderer.BindFrameBuffer(EFramebufferTarget.DrawFramebuffer, Data);");
        vkFrameBuffer.ShouldContain("Renderer.BindFrameBuffer(EFramebufferTarget.DrawFramebuffer, null);");

        bindFboCommand.ShouldContain("FrameBuffer.BindForWriting();");
        bindFboCommand.ShouldContain("PopCommand.Write = true;");
        bindFboCommand.ShouldNotContain("FrameBuffer.Bind();");
    }

    [Test]
    public void VulkanSwapchainOverlayPasses_LoadPresentedImageInsteadOfClearing()
    {
        string commandBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        commandBuffer.ShouldContain("static bool IsOverlayContext(FrameOpContext context)");
        commandBuffer.ShouldContain("context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline");
        commandBuffer.ShouldContain("CountLogicalSwapchainWriter(meshDraw.Context);");
        commandBuffer.ShouldContain("CountLogicalSwapchainWriter(blit.Context);");
        commandBuffer.ShouldNotContain("sceneSwapchainWriters = swapchainWriteCount;");

        commandBuffer.ShouldContain("(overlaySwapchainPass && imageWasEverPresentedAtRecordStart)");
        commandBuffer.ShouldContain("(legacyOverlaySwapchainPass && imageWasEverPresentedAtRecordStart)");
        commandBuffer.ShouldContain("void ExecuteDynamicUiBatchTextOverlay()");
        commandBuffer.ShouldContain("imageWasEverPresentedAtRecordStart;");
    }

    [Test]
    public void StereoMeshVersionSelection_PreservesAuthoredVertexShadersWithoutStereoVariants()
    {
        string meshRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs");

        meshRenderer.ShouldContain("bool canUseGeneratedStereoVertexShader = !MaterialHasAnyVertexShader();");
        meshRenderer.ShouldContain("private bool MaterialHasAnyVertexShader()");
        meshRenderer.ShouldContain("stereoPass && canUseGeneratedStereoVertexShader && RuntimeEngine.Rendering.State.HasAnyMultiViewExtension");
        meshRenderer.ShouldContain("stereoPass && canUseGeneratedStereoVertexShader && preferNV && RuntimeEngine.Rendering.State.IsNVIDIA");
    }

    [Test]
    public void TextureArrayFramebufferAttachments_CreateFullLayerSingleMipViews()
    {
        string textureArray = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture2DArray.cs");

        textureArray.ShouldContain("uint baseMip = ClampAttachmentMipLevel(mipLevel);");
        textureArray.ShouldContain("if (baseMip != 0 || ResolvedMipLevels > 1)");
        textureArray.ShouldContain("uint layerCount = Math.Max(ResolvedArrayLayers, 1u);");
        textureArray.ShouldContain("new AttachmentViewKey(baseMip, 1, 0, layerCount, ImageViewType.Type2DArray, AspectFlags)");
    }

    [Test]
    public void StereoAoAndBloomPasses_UseActiveCommandStateAndFramebufferUvSampling()
    {
        string gtao = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_GTAOPass.cs");
        gtao.ShouldContain("RuntimeEngine.Rendering.State.ActiveRenderCommandExecutionState");
        gtao.ShouldContain("renderState?.StereoRightEyeCamera as XRCamera");
        gtao.ShouldContain("ResolveActiveRenderSize(instance, out int width, out int height);");
        gtao.ShouldContain("instance?.RenderState.CurrentRenderRegion");

        string defaultPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        defaultPipeline.ShouldContain("activeState?.SceneCamera as XRCamera");
        defaultPipeline.ShouldContain("activeState?.RenderingCamera as XRCamera");

        string bloomPass = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_BloomPass.cs");
        bloomPass.ShouldContain("SetBloomViewportUniforms(program, instance);");
        bloomPass.ShouldContain("RuntimeEngine.Rendering.State.ActiveRenderCommandExecutionState");

        string lightCombinePass = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs");
        lightCombinePass.ShouldContain("RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy");

        string depthUtils = ReadWorkspaceFile("Build/CommonAssets/Shaders/Snippets/DepthUtils.glsl");
        depthUtils.ShouldContain("vec2 XRENGINE_ClipXYToFramebufferTextureUV(vec2 clipXY)");

        string deferredLightCombineStereo = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/DeferredLightCombineStereo.fs");
        deferredLightCombineStereo.ShouldContain("layout(location = 0) in vec3 FragPos");
        deferredLightCombineStereo.ShouldContain("XRENGINE_FramebufferUV(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight))");
        deferredLightCombineStereo.ShouldNotContain("XRENGINE_ClipXYToFramebufferTextureUV(FragPos.xy)");

        string postProcessStereo = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/PostProcessStereo.fs");
        postProcessStereo.ShouldContain("XRENGINE_FramebufferUV(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight))");
        postProcessStereo.ShouldNotContain("XRENGINE_ClipXYToFramebufferTextureUV(clipXY)");

        string[] bloomShaders =
        [
            "Build/CommonAssets/Shaders/Scene3D/BloomCopy.fs",
            "Build/CommonAssets/Shaders/Scene3D/BloomCopyStereo.fs",
            "Build/CommonAssets/Shaders/Scene3D/BloomDownsample.fs",
            "Build/CommonAssets/Shaders/Scene3D/BloomDownsampleStereo.fs",
            "Build/CommonAssets/Shaders/Scene3D/BloomUpsample.fs",
            "Build/CommonAssets/Shaders/Scene3D/BloomUpsampleStereo.fs",
        ];

        foreach (string shaderPath in bloomShaders)
        {
            string shader = ReadWorkspaceFile(shaderPath);
            shader.ShouldContain("XRENGINE_FramebufferUV(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight))");
            shader.ShouldNotContain("XRENGINE_ClipXYToFramebufferTextureUV(FragPos.xy)");
        }
    }

    [Test]
    public void DynamicRenderingLocalRead_IsQueriedReportedAndPlumbedAsDormantOptIn()
    {
        string logicalDevice = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");
        string extensions = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanExtensions.cs");
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderTargetMode.cs");
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string secondaryBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.SecondaryCommandBuffers.cs");
        string sync = ReadWorkspaceFile("XREngine.Runtime.Rendering/RenderGraph/RenderGraphSynchronization.cs");
        string barrierPlanner = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanBarrierPlanner.cs");
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");

        extensions.ShouldContain("\"VK_KHR_dynamic_rendering_local_read\"");
        extensions.ShouldContain("SupportsDynamicRenderingLocalRead");
        logicalDevice.ShouldContain("QueryDynamicRenderingLocalReadCapabilities");
        logicalDevice.ShouldContain("PhysicalDeviceDynamicRenderingLocalReadFeatures");
        logicalDevice.ShouldContain("PhysicalDeviceDynamicRenderingLocalReadFeaturesKHR");
        logicalDevice.ShouldContain("PhysicalDeviceVulkan14Properties");
        logicalDevice.ShouldContain("DynamicRenderingLocalReadDepthStencilAttachments");
        logicalDevice.ShouldContain("DynamicRenderingLocalReadMultisampledAttachments");

        sync.ShouldContain("RenderingLocalRead,");
        barrierPlanner.ShouldContain("RenderGraphImageLayout.RenderingLocalRead => ImageLayout.RenderingLocalRead");
        frameBuffer.ShouldContain("RenderGraphImageLayout.RenderingLocalRead => ImageLayout.RenderingLocalRead");

        modeSource.ShouldContain("internal readonly ref struct DynamicRenderingLocalReadPlan");
        modeSource.ShouldContain("ReadOnlySpan<uint> ColorAttachmentLocations");
        modeSource.ShouldContain("ReadOnlySpan<uint> ColorInputAttachmentIndices");
        commandBuffers.ShouldContain("RenderingAttachmentLocationInfo");
        commandBuffers.ShouldContain("RenderingInputAttachmentIndexInfo");
        commandBuffers.ShouldContain("TryAppendDynamicRenderingLocalReadPNext");
        commandBuffers.ShouldContain("plan.LocalRead.Enabled && SupportsDynamicRenderingLocalRead");
        commandBuffers.ShouldContain("CommandBufferInheritanceRenderingInfo");
        logicalDevice.ShouldContain("No pass has opted into local-read barriers yet.");
        secondaryBuffers.ShouldContain("TryAppendDynamicRenderingLocalReadPNext");
    }

    [Test]
    public void ModernVulkanCapabilitySnapshot_ReportsMatrixExtensionsAndStrictBackendRequests()
    {
        string logicalDevice = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");
        string extensions = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanExtensions.cs");
        string featureProfile = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanFeatureProfile.cs");
        string environment = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string initialization = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        string descriptorHeapBackend = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorHeap.cs");

        environment.ShouldContain(XREngineEnvironmentVariables.VkCapabilityTier);
        environment.ShouldContain(XREngineEnvironmentVariables.VkDescriptorBackend);
        environment.ShouldContain(XREngineEnvironmentVariables.VkProgramBindingBackend);
        environment.ShouldContain(XREngineEnvironmentVariables.VkFoveationBackend);
        environment.ShouldContain(XREngineEnvironmentVariables.VkRayTracingBackend);

        featureProfile.ShouldContain("EVulkanCapabilityTier");
        featureProfile.ShouldContain("EVulkanDescriptorBackend");
        featureProfile.ShouldContain("EVulkanProgramBindingBackend");
        featureProfile.ShouldContain("EVulkanFoveationBackend");
        featureProfile.ShouldContain("EVulkanRayTracingBackend");
        featureProfile.ShouldContain("EVulkanCapabilityState");
        featureProfile.ShouldContain("TryGetDescriptorBackendEnvOverride");

        logicalDevice.ShouldContain("ReportedModernCapabilityExtensionNames");
        logicalDevice.ShouldContain("foreach (string extensionName in ReportedModernCapabilityExtensionNames)");
        logicalDevice.ShouldContain("Capability.Extension name={0} available={1} enabled={2}");
        logicalDevice.ShouldContain("state=explicitly-required-missing");
        logicalDevice.ShouldContain("TryInitializeDescriptorHeapNativeApi");
        logicalDevice.ShouldContain("QueryDescriptorHeapCapabilities");
        logicalDevice.ShouldContain("PhysicalDeviceDescriptorHeapFeaturesEXTNative");
        logicalDevice.ShouldContain("ResolveDescriptorBackendAfterDeviceCreate");
        logicalDevice.ShouldContain("activeDescriptorBackend");
        logicalDevice.ShouldContain("ValidateExplicitModernBackendRequests");
        logicalDevice.ShouldContain("ThrowExplicitCapabilityMissing");
        logicalDevice.ShouldContain("native entry points, feature enablement, or heap storage initialization failed");
        descriptorHeapBackend.ShouldContain("Vulkan.DescriptorHeap.Capability");
        descriptorHeapBackend.ShouldContain("Vulkan.DescriptorHeap.Allocation");
        descriptorHeapBackend.ShouldContain("Vulkan.DescriptorHeap.Active");
        logicalDevice.ShouldContain("ShaderUntypedPointers");
        logicalDevice.ShouldContain("Capability.Snapshot apiVersion=");
        logicalDevice.ShouldContain("enabled-active");
        logicalDevice.ShouldContain("enabled-unused");
        logicalDevice.ShouldContain("available-disabled");
        logicalDevice.ShouldContain("unavailable");

        initialization.ShouldContain("InitializeSynchronizationBackend();");
        initialization.ShouldContain("LogStartupCapabilitySnapshot();");
        initialization.IndexOf("InitializeSynchronizationBackend();", StringComparison.Ordinal)
            .ShouldBeLessThan(initialization.IndexOf("LogStartupCapabilitySnapshot();", StringComparison.Ordinal));

        string optionalExtensions = SliceArrayInitializer(extensions, "optionalDeviceExtensions");
        string reportedExtensions = SliceArrayInitializer(extensions, "ReportedModernCapabilityExtensionNames");
        foreach (Match match in Regex.Matches(optionalExtensions, "\"([^\"]+)\""))
            reportedExtensions.ShouldContain(match.Groups[1].Value);

        reportedExtensions.ShouldContain("VK_EXT_depth_clip_control");
        reportedExtensions.ShouldContain("VK_EXT_transform_feedback");
        reportedExtensions.ShouldContain("VK_EXT_descriptor_heap");
        reportedExtensions.ShouldContain("VK_KHR_shader_untyped_pointers");
        reportedExtensions.ShouldContain("VK_EXT_shader_object");
        reportedExtensions.ShouldContain("VK_KHR_dynamic_rendering_local_read");
        reportedExtensions.ShouldContain("VK_KHR_maintenance5");
        reportedExtensions.ShouldContain("VK_KHR_extended_flags");
        reportedExtensions.ShouldContain("VK_EXT_device_generated_commands");
    }

    [Test]
    public void DescriptorHeapPhase13_DeclaresNativeInteropMappingPayloadsAndActiveBackend()
    {
        string native = ReadWorkspaceDirectory("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorHeapNative", "*.cs");
        string backend = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorHeap.cs");
        string bindings = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorHeapBindings.cs");
        string logicalDevice = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");
        string commandState = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferState.cs");
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string secondaryBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.SecondaryCommandBuffers.cs");
        string featureProfile = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanFeatureProfile.cs");
        string program = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string material = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string meshDescriptors = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string meshDrawing = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string imgui = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");

        native.ShouldContain("public const string ExtensionName = \"VK_EXT_descriptor_heap\"");
        native.ShouldContain("public const string ShaderUntypedPointersExtensionName = \"VK_KHR_shader_untyped_pointers\"");
        native.ShouldContain("DescriptorHeapBufferUsage = (BufferUsageFlags)(1u << 28)");
        native.ShouldContain("PipelineCreate2DescriptorHeapBit = 1ul << 36");
        native.ShouldContain("PhysicalDeviceDescriptorHeapPropertiesEXTNative");
        native.ShouldContain("PhysicalDeviceDescriptorHeapFeaturesEXTNative");
        native.ShouldContain("ResourceDescriptorInfoEXTNative");
        native.ShouldContain("BindHeapInfoEXTNative");
        native.ShouldContain("PushDataInfoEXTNative");
        native.ShouldContain("ShaderDescriptorSetAndBindingMappingInfoEXTNative");
        native.ShouldContain("CommandBufferInheritanceDescriptorHeapInfoEXTNative");
        native.ShouldContain("PipelineCreateFlags2CreateInfoNative");

        backend.ShouldContain("vkCmdBindSamplerHeapEXT");
        backend.ShouldContain("vkCmdBindResourceHeapEXT");
        backend.ShouldContain("vkCmdPushDataEXT");
        backend.ShouldContain("vkWriteSamplerDescriptorsEXT");
        backend.ShouldContain("vkWriteResourceDescriptorsEXT");
        backend.ShouldContain("vkGetPhysicalDeviceDescriptorSizeEXT");
        backend.ShouldContain("CreateDescriptorHeapStorage(\"Sampler\"");
        backend.ShouldContain("CreateDescriptorHeapStorage(\"Resource\"");
        backend.ShouldContain("VulkanDescriptorHeapExt.DescriptorHeapBufferUsage");
        backend.ShouldContain("BufferUsageFlags.ShaderDeviceAddressBit");
        backend.ShouldContain("TryWriteDescriptorHeapSamplerDescriptors");
        backend.ShouldContain("TryWriteDescriptorHeapResourceDescriptors");
        backend.ShouldContain("TryPushDescriptorHeapData");
        backend.ShouldContain("TryAppendDescriptorHeapInheritancePNext");
        backend.ShouldContain("_activeDescriptorBackend == EVulkanDescriptorBackend.DescriptorHeap");
        backend.ShouldContain("Descriptor heap is the active descriptor backend.");
        backend.ShouldContain("DeviceLocalWithStaging");
        backend.ShouldContain("FlushDescriptorHeapStagingCopies(commandBuffer)");
        backend.ShouldContain("VulkanDescriptorHeapExt.ResourceHeapReadAccess2");
        backend.ShouldContain("DescriptorHeapLastFrameCopies");

        bindings.ShouldContain("CreateDescriptorHeapProgramLayout");
        bindings.ShouldContain("VulkanDescriptorMappingSourceEXT.HeapWithPushIndex");
        bindings.ShouldContain("TryWriteDescriptorHeapBinding");
        bindings.ShouldContain("TryWriteDescriptorHeapCombinedImageSamplerPayload");
        bindings.ShouldContain("DescriptorHeapPushDataPayload");
        bindings.ShouldContain("TryGetDescriptorHeapImageViewCreateInfo");
        bindings.ShouldContain("TryGetDescriptorHeapSamplerCreateInfo");
        bindings.ShouldContain("TryGetDescriptorHeapBufferViewCreateInfo");

        logicalDevice.ShouldContain("descriptorHeapDependenciesReady");
        logicalDevice.ShouldContain("shaderUntypedPointersExtensionAvailable");
        logicalDevice.ShouldContain("descriptorHeapFeatureEnable");
        logicalDevice.ShouldContain("ResolveDescriptorBackendAfterDeviceCreate");
        commandState.ShouldContain("DescriptorHeapSignature");
        commandState.ShouldContain("InvalidateDescriptorHeapBindingState");
        commandState.ShouldContain("InvalidateDescriptorSetBindingState");
        commandState.ShouldContain("TryBindDescriptorHeapsTracked(commandBuffer)");
        commandBuffers.ShouldContain("TryAppendDescriptorHeapInheritancePNext");
        commandBuffers.ShouldContain("TryBuildAndBindComputeDescriptorSets");
        secondaryBuffers.ShouldContain("TryAppendDescriptorHeapInheritancePNext");
        featureProfile.ShouldContain(": EVulkanDescriptorBackend.DescriptorIndexing");
        program.ShouldContain("CreateDescriptorHeapProgramLayout");
        program.ShouldContain("ShaderDescriptorSetAndBindingMappingInfoEXTNative");
        program.ShouldContain("PipelineCreate2DescriptorHeapBit");
        program.ShouldContain("TryPushDescriptorHeapProgramData");
        material.ShouldContain("DescriptorHeapPushData");
        material.ShouldContain("TryWriteDescriptorHeapBinding");
        meshDescriptors.ShouldContain("TryWriteDescriptorHeapBinding");
        meshDrawing.ShouldContain("TryPushDescriptorHeapProgramData");
        imgui.ShouldContain("TryWriteDescriptorHeapCombinedImageSamplerPayload");
        imgui.ShouldContain("ResolveImGuiDescriptorHeapPayload");
    }

    [Test]
    public void DynamicRenderingResolveAttachments_AreMappedToNativeResolveFieldsAndValidated()
    {
        string usage = ReadWorkspaceFile("XREngine.Runtime.Rendering/RenderGraph/RenderPassResourceUsage.cs");
        string builder = ReadWorkspaceFile("XREngine.Runtime.Rendering/RenderGraph/RenderPassBuilder.cs");
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderTargetMode.cs");
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string renderPasses = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Framebuffers/VulkanRenderer.FrameBufferRenderPasses.cs");

        usage.ShouldContain("public uint? ResolveSourceColorIndex");
        builder.ShouldContain("UseResolveAttachment(string resourceName, uint sourceColorIndex");
        builder.ShouldContain("resolveSourceColorIndex");

        modeSource.ShouldContain("public DynamicRenderingAttachmentPlan WithResolve");
        modeSource.ShouldContain("ResolveImageView = ResolveImageView");
        commandBuffers.ShouldContain("resolveAttachmentPlans[resolveAttachmentCount]");
        commandBuffers.ShouldContain("colorAttachmentPlans[sourcePlanIndex].WithResolve");
        commandBuffers.ShouldContain("ResolveModeFlags.AverageBit");
        commandBuffers.ShouldContain("if (signatures[i].Role == AttachmentRole.Color && signatures[i].Samples != default)");
        commandBuffers.ShouldContain("signatures[i].Role != AttachmentRole.Resolve");

        frameBuffer.ShouldContain("AttachmentRole.Resolve");
        frameBuffer.ShouldContain("ResolveResolveSourceColorIndex");
        frameBuffer.ShouldContain("ValidateResolveAttachmentPairings");
        frameBuffer.ShouldContain("Vulkan resolve sources must be multisampled");
        frameBuffer.ShouldContain("Vulkan resolve targets must be single-sampled");
        frameBuffer.ShouldContain("format/aspect");
        renderPasses.ShouldContain("PResolveAttachments = resolveRefs.Length > 0 ? resolvePtr : null");
        renderPasses.ShouldContain("Attachment = uint.MaxValue");
    }

    [Test]
    public void BarrierPlanner_TracksSwapchainPseudoResourceWithoutPhysicalImageGroup()
    {
        string planner = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanBarrierPlanner.cs");
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        planner.ShouldContain("private readonly List<PlannedSwapchainBarrier> _swapchainBarriers");
        planner.ShouldContain("public IReadOnlyList<PlannedSwapchainBarrier> GetSwapchainBarriersForPass");
        planner.ShouldContain("IsSwapchainTargetUsage(usage, resourcePlanner)");
        planner.ShouldContain("TrackSwapchainUsage(pass, usage, edge, ownership)");
        planner.ShouldContain("PlannedImageState.FromSwapchainUsage");
        planner.ShouldContain("PlannedImageState.SwapchainPresentInitial()");
        planner.ShouldContain("internal readonly record struct PlannedSwapchainBarrier");
        planner.ShouldContain("ResourceName.Equals(RenderGraphResourceNames.OutputRenderTarget");
        planner.ShouldContain("yield break; // swapchain target handled separately");

        commandBuffers.ShouldContain("var plannedSwapchainBarriers = BarrierPlanner.GetSwapchainBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex)");
        commandBuffers.ShouldContain("EmitPlannedSwapchainBarriers(commandBuffer, plannedSwapchainBarriers)");
        commandBuffers.ShouldContain("var swapchainBarriers = BarrierPlanner.GetSwapchainBarriersForPass(passIndex)");
        commandBuffers.ShouldContain("EmitPlannedSwapchainBarriers(commandBuffer, swapchainBarriers)");
        commandBuffers.ShouldContain("ImageLayout liveOldLayout = ResolveCurrentSwapchainColorLayout()");
    }

    [Test]
    public void TransientAttachmentPolicy_RequestsLazyMemoryOnlyForAttachmentOnlyTransientImages()
    {
        string planner = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanResourcePlanner.cs");
        string allocator = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/VulkanResourceAllocator.cs");
        string registration = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/VulkanRenderer.ResourceRegistration.cs");
        string initialization = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");

        planner.ShouldContain("internal enum VulkanTransientAttachmentPolicy");
        planner.ShouldContain("PreferLazilyAllocated");
        planner.ShouldContain("requiresPersistentShaderOrTransferAccess");
        planner.ShouldContain("RenderPipelineResourceUsage.SampledTexture");
        planner.ShouldContain("RenderPipelineResourceUsage.StorageImage");
        planner.ShouldContain("RenderPipelineResourceUsage.TransferSource");
        planner.ShouldContain("RenderPipelineResourceUsage.TransferDestination");

        allocator.ShouldContain("ImageUsageFlags.TransientAttachmentBit");
        allocator.ShouldContain("usage &= ~(ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit)");
        allocator.ShouldContain("MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.LazilyAllocatedBit");
        allocator.ShouldContain("public MemoryPropertyFlags MemoryProperties");
        allocator.ShouldContain("public VulkanTransientAttachmentPolicy TransientAttachmentPolicy");

        registration.ShouldContain("group.MemoryProperties");
        registration.ShouldContain("requested lazy memory");
        initialization.ShouldContain("lazy allocation failed; falling back");
    }

    [Test]
    public void DynamicRenderingAttachmentTransitions_UseLayoutCompatibleStageAccessMasks()
    {
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        commandBuffers.ShouldContain("NormalizeFboAttachmentLayout(");
        commandBuffers.ShouldContain("ImageLayout.ColorAttachmentOptimal => ImageLayout.DepthStencilAttachmentOptimal");
        commandBuffers.ShouldContain("ImageLayout.ShaderReadOnlyOptimal => ImageLayout.DepthStencilReadOnlyOptimal");
        commandBuffers.ShouldContain("if (layout == ImageLayout.ShaderReadOnlyOptimal)");
        commandBuffers.ShouldContain("return PipelineStageFlags.FragmentShaderBit;");
        commandBuffers.ShouldContain("if (layout == ImageLayout.TransferSrcOptimal)");
        commandBuffers.ShouldContain("return AccessFlags.ShaderReadBit;");
        commandBuffers.ShouldContain("access |= AccessFlags.ShaderReadBit;");
        commandBuffers.ShouldNotContain("if (signature.Role == AttachmentRole.Color || layout == ImageLayout.ColorAttachmentOptimal)");
    }

    [Test]
    public void DynamicRenderingDepthAttachments_NormalizeFormatRoleAspectAndGraphLayouts()
    {
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string blit = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");

        frameBuffer.ShouldContain("ResolveAttachmentRole(attachment, source.AspectMask, source.Format)");
        frameBuffer.ShouldContain("NormalizeAttachmentAspectMask(source.DescriptorFormat, source.DescriptorAspect)");
        frameBuffer.ShouldContain("VkFormatConversions.IsDepthStencilFormat(source.DescriptorFormat)");
        frameBuffer.ShouldContain("RenderGraphImageLayout.ColorAttachment => IsColorLikeAttachmentRole(signature.Role)");
        frameBuffer.ShouldContain(": ImageLayout.DepthStencilAttachmentOptimal");
        blit.ShouldContain("or Format.S8Uint");
        blit.ShouldContain("if (!IsDepthOrStencilFormat(format))");
        blit.ShouldContain("Format.S8Uint => ImageAspectFlags.StencilBit");
    }

    [Test]
    public void RetiredImageResources_AreDeduplicatedBeforeDestroy()
    {
        string retirementSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");

        retirementSource.ShouldContain("private readonly HashSet<ulong>[] _retiredImageHandles");
        retirementSource.ShouldContain("private readonly HashSet<ulong>[] _retiredImageMemoryHandles");
        retirementSource.ShouldContain("private readonly HashSet<ulong>[] _retiredImageViewHandles");
        retirementSource.ShouldContain("private readonly HashSet<ulong>[] _retiredSamplerHandles");
        retirementSource.ShouldContain("private ImageView[] FilterRetiredAttachmentViews");
        retirementSource.ShouldContain("_retiredImageHandles[frameSlot].Add(image.Handle)");
        retirementSource.ShouldContain("_retiredImageMemoryHandles[frameSlot].Add(memory.Handle)");
        retirementSource.ShouldContain("_retiredImageViewHandles[frameSlot].Add(primaryView.Handle)");
        retirementSource.ShouldContain("_retiredSamplerHandles[frameSlot].Add(sampler.Handle)");
        retirementSource.ShouldContain("CompleteRetiredImageDeduplication(frameSlot, in r)");
        retirementSource.ShouldContain("_retiredImageHandles[frameSlot].Remove(resources.Image.Handle)");
        retirementSource.ShouldContain("TryBeginDestroyVulkanResourceGeneration(");
        retirementSource.ShouldContain("entry.ImageGeneration");
        retirementSource.ShouldContain("entry.SamplerGeneration");
        retirementSource.ShouldContain("_imageAllocations.TryRemove(r.Image.Handle, out trackedImageAllocation)");
        retirementSource.ShouldContain("FreeMemoryAllocation(trackedImageAllocation)");
        retirementSource.ShouldContain("Skipping raw vkFreeMemory for unowned/stale image memory");
        retirementSource.ShouldNotContain("Api!.FreeMemory(device, memory, null)");
        retirementSource.ShouldContain("freedMemories++;");
    }

    [Test]
    public void DynamicPipelines_AreKeyedByAttachmentFormatSignatureWithoutRenderPassHandles()
    {
        string pipelineKey = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.PipelineKey.cs");
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string prewarm = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelinePrewarmDatabase.cs");
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderTargetMode.cs");

        pipelineKey.ShouldContain("DynamicRenderingFormatSignature DynamicRenderingFormats");
        meshPipeline.ShouldContain("useDynamicRendering ? 0UL : renderPass.Handle");
        meshPipeline.ShouldContain("dynamicRenderingFormats.GetColorAttachmentFormat");
        meshPipeline.ShouldContain("dynamicRenderingFormats.CopyColorAttachmentFormats");
        meshPipeline.ShouldContain("DepthAttachmentFormat = request.DynamicRenderingFormats.DepthAttachmentFormat");
        meshPipeline.ShouldContain("StencilAttachmentFormat = request.DynamicRenderingFormats.StencilAttachmentFormat");
        prewarm.ShouldContain("BuildDynamicRenderingSignature(dynamicRenderingFormats)");
        prewarm.ShouldContain("dynamicRenderingFormats.DescribeColorFormats()");
        modeSource.ShouldContain("DescribeColorFormats()");
    }

    [Test]
    public void GraphicsPipelineLibraryExtension_EnablesRequiredKhrDependency()
    {
        string extensionsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanExtensions.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");

        extensionsSource.ShouldContain("\"VK_KHR_pipeline_library\"");
        extensionsSource.ShouldContain("\"VK_EXT_graphics_pipeline_library\"");
        logicalDeviceSource.ShouldContain("optionalExt == \"VK_EXT_graphics_pipeline_library\"");
        logicalDeviceSource.ShouldContain("!availableExtensionSet.Contains(\"VK_KHR_pipeline_library\")");
        logicalDeviceSource.ShouldContain("graphicsPipelineLibraryDependencyEnabled");
        logicalDeviceSource.ShouldContain("extensionsArray.Contains(\"VK_KHR_pipeline_library\")");
    }

    [Test]
    public void GraphicsPipelineLibraryKeys_AreSubsetScopedAndPendingLinksAreNotLoggedAsFailures()
    {
        string graphicsLibraryKey = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.GraphicsPipelineLibraryKey.cs");
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

        graphicsLibraryKey.ShouldContain("internal readonly record struct GraphicsPipelineLibraryKey(");
        graphicsLibraryKey.ShouldContain("GraphicsPipelineLibrarySubset Subset,");
        graphicsLibraryKey.ShouldContain("DynamicRenderingFormatSignature DynamicRenderingFormats,");
        meshPipeline.ShouldContain("CreateGraphicsPipelineLibraryKey(GraphicsPipelineLibrarySubset.VertexInputInterface, request.Key)");
        meshPipeline.ShouldContain("hasProgram = subset is GraphicsPipelineLibrarySubset.PreRasterizationShaders or GraphicsPipelineLibrarySubset.FragmentShader");
        meshPipeline.ShouldContain("hasDepthStencil = subset is GraphicsPipelineLibrarySubset.FragmentShader or GraphicsPipelineLibrarySubset.FragmentOutputInterface");
        meshPipeline.ShouldContain("hasBlendState = subset == GraphicsPipelineLibrarySubset.FragmentOutputInterface");
        meshPipeline.ShouldContain("DynamicRenderingFormatSignature dynamicRenderingFormats = CreateGraphicsPipelineLibraryDynamicRenderingFormatSignature(subset, pipeline);");
        meshPipeline.ShouldContain("CreateGraphicsPipelineLibraryDynamicRenderingFormatSignature(");
        meshPipeline.ShouldContain("pipeline.DynamicRenderingFormats.ViewMask");
        meshPipeline.ShouldContain("GraphicsPipelineLibrarySubset.FragmentOutputInterface => pipeline.DynamicRenderingFormats");
        meshPipeline.ShouldContain("bool includeDynamicRenderingInfo = key.UseDynamicRendering;");
        meshPipeline.ShouldContain("PipelineRenderingCreateInfo libraryRenderingInfo = default;");
        meshPipeline.ShouldContain("PNext = includeDynamicRenderingInfo ? &libraryRenderingInfo : null");
        meshPipeline.ShouldContain("ApplyGraphicsPipelineLibrarySubset(ref libraryPipelineInfo, key.Subset)");
        meshPipeline.ShouldContain("linkedRenderingInfo.PNext = &libraryInfo;");
        meshPipeline.ShouldContain("linkedInfo.PNext = &linkedRenderingInfo;");
        meshPipeline.ShouldNotContain("PNext = pipelineInfo.PNext");
        meshPipeline.ShouldContain("case GraphicsPipelineLibrarySubset.PreRasterizationShaders:");
        meshPipeline.ShouldContain("pipelineInfo.PDepthStencilState = null;");
        meshPipeline.ShouldContain("pipelineInfo.PColorBlendState = null;");
        meshPipeline.ShouldNotContain("linkedInfo.PDepthStencilState = null;");
        meshPipeline.ShouldNotContain("linkedInfo.PColorBlendState = null;");

        meshPipeline.ShouldContain("XRRenderProgram.ShaderProgramBackendStatus backend = _program.Data.ShaderMetadata.Backend");
        meshPipeline.ShouldContain("backend.Stage == XRRenderProgram.EShaderProgramBackendStage.Failed");
        meshPipeline.ShouldContain("program link failed");
    }

    [Test]
    public void DynamicRenderingDepthOnlyPasses_CreatePipelinesInsteadOfSkippingDraws()
    {
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

        meshPipeline.ShouldContain("ResolveAttachmentCompatibleDrawState(");
        meshPipeline.ShouldContain("colorAttachmentCount == 0");
        meshPipeline.ShouldContain("ColorWriteMask = 0");
        meshPipeline.ShouldContain("BlendEnabled = false");
        meshPipeline.ShouldContain("AlphaToCoverageEnabled = false");
        meshPipeline.ShouldContain("if (colorAttachmentCount == 0)");
        meshPipeline.ShouldContain("stages = stages.Where(static stage => stage.Stage != ShaderStageFlags.FragmentBit).ToArray();");
        meshPipeline.ShouldContain("Vulkan.PipelineLibrary.DepthOnlyMonolithic");
        meshPipeline.ShouldContain("graphics pipeline libraries are bypassed for zero-color pipelines");
        meshPipeline.ShouldContain("return CreateMonolithicGraphicsPipeline(request, ref pipelineInfo, pipelineCache);");
        meshPipeline.ShouldNotContain("Vulkan.MeshRenderer.SkipDraw.NoColorAttachment");
        meshPipeline.ShouldNotContain("dynamic rendering has undefined color attachment format while color writes are enabled");
    }

    [Test]
    public void GeneratedProgramIdentity_UsesStableShaderAndVariantIdentityInsteadOfMaterialRevision()
    {
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

        meshPipeline.ShouldContain("BuildShaderIdentityList(material, generatedVertexIdentity)");
        meshPipeline.ShouldContain("material.ActiveUberVariant.VariantHash.ToString(\"X16\")");
        meshPipeline.ShouldContain("StringComparer.Ordinal.GetHashCode(sourceText)");
        meshPipeline.ShouldNotContain(";shaderRevision=");
        meshPipeline.ShouldNotContain("material.ShaderStateRevision.ToString");
    }

    [Test]
    public void SynchronousDepthReadback_UsesBoundFramebufferBeforeSwapchainFallback()
    {
        string readback = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Readback.cs");
        string blit = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");

        int getDepthIndex = readback.IndexOf("public override float GetDepth(int x, int y)", StringComparison.Ordinal);
        int boundFramebufferIndex = readback.IndexOf("boundReadFrameBuffer is not null", getDepthIndex, StringComparison.Ordinal);
        int swapchainFallbackIndex = readback.IndexOf("TryReadSwapchainDepthPixel", getDepthIndex, StringComparison.Ordinal);
        int depthReadIndex = blit.IndexOf("private bool TryReadDepthPixel", StringComparison.Ordinal);
        int liveDepthIndex = blit.IndexOf("TryResolveLiveBlitImage(source, out BlitImageInfo liveSource)", depthReadIndex, StringComparison.Ordinal);
        int liveDepthCopyIndex = blit.IndexOf("liveSource.Image", liveDepthIndex, StringComparison.Ordinal);

        getDepthIndex.ShouldBeGreaterThanOrEqualTo(0);
        boundFramebufferIndex.ShouldBeGreaterThan(getDepthIndex);
        swapchainFallbackIndex.ShouldBeGreaterThan(boundFramebufferIndex);
        readback.ShouldContain("TryResolveBlitImage(");
        readback.ShouldContain("wantDepth: true");
        readback.ShouldContain("TryReadDepthPixel(depthSource, x, y, out float fboDepth)");
        readback.ShouldContain("Vulkan.Readback.DepthBoundFboFailed");
        depthReadIndex.ShouldBeGreaterThanOrEqualTo(0);
        liveDepthIndex.ShouldBeGreaterThan(depthReadIndex);
        liveDepthCopyIndex.ShouldBeGreaterThan(liveDepthIndex);
    }

    [Test]
    public void EditorDepthHit_ConvertsVulkanReadbackToTopLeftFramebufferCoordinates()
    {
        string editorPawn = ReadWorkspaceFile("XREngine.Editor/EditorFlyingCameraPawnComponent.cs");

        editorPawn.ShouldContain("GetDepthReadbackCoordinate(fbo, internalSizeCoordinate)");
        editorPawn.ShouldContain("RuntimeRenderingHostServices.Current.CurrentRenderBackend");
        editorPawn.ShouldContain("RenderClipSpacePolicy.FramebufferTextureYDirection(backend)");
        editorPawn.ShouldContain("int maxY = Math.Max((int)fbo.Height - 1, 0);");
        editorPawn.ShouldContain("coordinate.Y = maxY - coordinate.Y;");
    }

    [Test]
    public void CommonPushConstants_AreVisibleToGeometryShaders()
    {
        string commandState = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferState.cs");
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string renderProgram = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string programPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgramPipeline.cs");
        string meshDrawing = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

        commandState.ShouldContain("internal const ShaderStageFlags CommonPushConstantStageFlags");
        commandState.ShouldContain("ShaderStageFlags.GeometryBit |");
        commandState.ShouldContain("ShaderStageFlags.TessellationEvaluationBit |");
        commandBuffers.ShouldContain("CommonPushConstantStageFlags,");
        renderProgram.ShouldContain("StageFlags = CommonPushConstantStageFlags");
        programPipeline.ShouldContain("StageFlags = CommonPushConstantStageFlags");
        meshDrawing.ShouldContain("CommonPushConstantStageFlags,");
    }

    [Test]
    public void FboDepthStencilMetadata_PreservesStencilForOnTopAndPostProcessPasses()
    {
        string viewportCommand = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/ViewportRenderCommand.cs");
        string quadBlit = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderQuadToFBO.Internal.cs");
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string bindFbo = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/State/VPRC_BindFBOByName.cs");

        viewportCommand.ShouldContain("MakeFboStencilResource(target.Name)");
        viewportCommand.ShouldContain("builder.UseStencilAttachment(");
        viewportCommand.ShouldNotContain("RenderTargetHasStencilAttachment");

        int sharedDepthIndex = quadBlit.IndexOf("if (resources?.UseDestinationDepthStencil == true)", StringComparison.Ordinal);
        int sharedStencilIndex = sharedDepthIndex >= 0
            ? quadBlit.IndexOf("MakeFboStencilResource(destination)", sharedDepthIndex, StringComparison.Ordinal)
            : -1;
        sharedDepthIndex.ShouldBeGreaterThanOrEqualTo(0);
        sharedStencilIndex.ShouldBeGreaterThan(sharedDepthIndex);
        quadBlit.ShouldContain("ERenderGraphAccess.Read");

        frameBuffer.ShouldContain("if (usage.ResourceType == ERenderPassResourceType.StencilAttachment)");
        frameBuffer.ShouldContain("return [];");

        bindFbo.ShouldContain("string stencilResource = MakeFboStencilResource(frameBufferName);");
        bindFbo.ShouldContain("usage.ResourceType == ERenderPassResourceType.StencilAttachment");
        bindFbo.ShouldContain("string.Equals(usage.ResourceName, stencilResource, StringComparison.OrdinalIgnoreCase)");
    }

    [Test]
    public void ReadOnlyDepthStencilCompatibility_DoesNotStripGizmoStencilWritesFromMergedPasses()
    {
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");

        string passUsesReadOnly = SliceMethod(meshPipeline, "private static bool PassUsesReadOnlyDepthStencil(");
        passUsesReadOnly.ShouldContain("bool hasDepthStencilWriteUsage = false;");
        passUsesReadOnly.ShouldContain("usage.Access is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite");
        passUsesReadOnly.ShouldContain("return hasDepthStencilUsage && !hasDepthStencilWriteUsage;");

        frameBuffer.ShouldContain("HashSet<int> writeCapableDepthStencilAttachments = CollectWriteCapableDepthStencilAttachments(planned, pass, frameBufferName);");
        frameBuffer.ShouldContain("ResolveAttachmentReferenceLayout(updated, usage, writeCapableDepthStencilAttachments.Contains(index))");

        string collectWrites = SliceMethod(frameBuffer, "private static HashSet<int> CollectWriteCapableDepthStencilAttachments(");
        collectWrites.ShouldContain("usage.Access == ERenderGraphAccess.Read");
        collectWrites.ShouldContain("ResolveMatchingAttachmentIndices(signatures, slot, usage, pass)");

        string referenceLayout = SliceMethod(frameBuffer, "private static ImageLayout ResolveAttachmentReferenceLayout(");
        referenceLayout.ShouldContain("usage.Access == ERenderGraphAccess.Read && !passHasWriteCapableDepthStencilUsage");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            string marker = $"{Path.DirectorySeparatorChar}Commands{Path.DirectorySeparatorChar}VulkanRenderer.";
            path = path.Replace(
                marker,
                $"{Path.DirectorySeparatorChar}Commands{Path.DirectorySeparatorChar}CommandBuffers{Path.DirectorySeparatorChar}VulkanRenderer.",
                StringComparison.Ordinal);
        }
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
        return File.ReadAllText(path);
    }

    private static string ReadWorkspaceDirectory(string relativePath, string searchPattern)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(path).ShouldBeTrue($"Expected workspace directory '{path}' to exist.");

        string[] files = Directory.GetFiles(path, searchPattern, SearchOption.AllDirectories);
        files.Length.ShouldBeGreaterThan(0, $"Expected '{path}' to contain files matching '{searchPattern}'.");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return string.Join(Environment.NewLine, files.Select(File.ReadAllText));
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }

    private static string SliceMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Could not find method signature '{signature}'.");

        int openBrace = source.IndexOf('{', start);
        openBrace.ShouldBeGreaterThanOrEqualTo(start, $"Could not find method body for '{signature}'.");

        int depth = 0;
        for (int i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
                depth--;

            if (depth == 0)
                return source[start..(i + 1)];
        }

        throw new InvalidOperationException($"Could not find method end for '{signature}'.");
    }

    private static string SliceArrayInitializer(string source, string fieldName)
    {
        int fieldIndex = source.IndexOf(fieldName, StringComparison.Ordinal);
        fieldIndex.ShouldBeGreaterThanOrEqualTo(0, $"Could not find array field '{fieldName}'.");

        int openBracket = source.IndexOf('[', fieldIndex);
        openBracket.ShouldBeGreaterThanOrEqualTo(fieldIndex, $"Could not find initializer start for '{fieldName}'.");

        int close = source.IndexOf("];", openBracket, StringComparison.Ordinal);
        close.ShouldBeGreaterThan(openBracket, $"Could not find initializer end for '{fieldName}'.");

        return source[openBracket..(close + 2)];
    }
}
