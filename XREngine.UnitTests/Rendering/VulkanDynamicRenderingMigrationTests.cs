using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanDynamicRenderingMigrationTests
{
    [Test]
    public void RenderTargetMode_HasEnvironmentOverrideAndVisibleUnsupportedDynamicFailure()
    {
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Output/Authority/VulkanOutputRuntime.RenderTargetModePolicy.cs");
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.cs");
        string environmentSource = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");
        string smokeControllerSource = ReadWorkspaceFile("XREngine.Editor/Program.OpenXrSmokeRunController.cs");

        modeSource.ShouldContain("XREngineEnvironmentVariables.VkRenderTargetMode");
        environmentSource.ShouldContain(XREngineEnvironmentVariables.VkRenderTargetMode);
        modeSource.ShouldContain("EVulkanRenderTargetMode.Auto");
        modeSource.ShouldContain("EVulkanRenderTargetMode.DynamicRendering");
        modeSource.ShouldContain("EVulkanRenderTargetMode.LegacyRenderPass");
        logicalDeviceSource.ShouldContain("dynamic rendering was explicitly requested");
        rendererSource.ShouldContain("public EVulkanRenderTargetMode EffectiveRenderTargetMode");
        rendererSource.ShouldContain("? EVulkanRenderTargetMode.DynamicRendering");
        smokeControllerSource.ShouldContain("EditorRendererCapabilityResolver.TryGetForBackend(");
        smokeControllerSource.ShouldContain("diagnostics.EffectiveRenderTargetMode");
        logicalDeviceSource.ShouldContain("_outputRuntime.ResolveRenderTargetMode(_deviceContext);");
        logicalDeviceSource.ShouldContain("[Vulkan] Render target mode:");
    }

    [Test]
    public void DynamicCommandRecording_UsesDynamicRenderingAndKeepsLegacyCallsModeGated()
    {
        string recording = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.RenderScopes.cs");
        string nativeRecording = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs");
        string desktopOutput = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Output/Authority/VulkanDesktopSwapchainService.RenderPasses.cs");

        recording.ShouldContain("recordingState.Policy.UseDynamicRendering &&");
        recording.ShouldContain("if (useDynamicRendering)");
        recording.ShouldContain("BeginDynamicRenderingScope(");
        recording.ShouldContain("CmdEndDynamicRendering(");
        nativeRecording.ShouldContain("Api.CmdBeginRendering(commandBuffer, renderingInfo);");
        nativeRecording.ShouldContain("dynamicRendering.CmdBeginRendering(commandBuffer, renderingInfo);");
        recording.ShouldContain("TransitionFboAttachmentsForDynamicRendering(");
        recording.ShouldContain("CmdBeginRenderPassTracked(");
        recording.ShouldContain("&fboPassInfo, secondaryContents ? SubpassContents.SecondaryCommandBuffers : SubpassContents.Inline");
        desktopOutput.ShouldContain("if (_device.MutableCapabilities._useDynamicRenderingRenderTargets)");
        desktopOutput.ShouldContain("_resources.SwapchainRenderPass = default;");
        desktopOutput.ShouldContain("_output.Desktop.Framebuffers = new Framebuffer[imageViews.Length];");
    }

    [Test]
    public void DynamicCommandRecording_UsesSharedScopeAndAttachmentPlans()
    {
        string commandBuffers = SourceContractWorkspace.ReadVulkanCommandRuntimeSource();
        string attachmentPlan = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/DynamicRenderingAttachmentPlan.cs");
        string scopePlan = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/DynamicRenderingScopePlan.cs");

        attachmentPlan.ShouldContain("internal readonly struct DynamicRenderingAttachmentPlan");
        attachmentPlan.ShouldContain("ImageView ResolveImageView");
        attachmentPlan.ShouldContain("ResolveModeFlags ResolveMode");
        scopePlan.ShouldContain("internal readonly ref struct DynamicRenderingScopePlan");
        scopePlan.ShouldContain("ReadOnlySpan<DynamicRenderingAttachmentPlan> ColorAttachments");
        scopePlan.ShouldContain("SampleCountFlags SampleCount");

        commandBuffers.ShouldContain("scoped in DynamicRenderingScopePlan plan,\n            bool secondaryContents,");
        commandBuffers.ShouldContain("colorAttachments[i] = colorPlans[i].ToRenderingAttachmentInfo()");
        commandBuffers.ShouldContain("Span<DynamicRenderingAttachmentPlan> colorAttachmentPlans = stackalloc DynamicRenderingAttachmentPlan[1];");
        commandBuffers.ShouldContain("colorAttachmentPlans[..colorAttachmentCount]");
        commandBuffers.ShouldContain("ResolveDynamicRenderingSampleCount(fboSignature)");
        commandBuffers.ShouldContain("BeginDynamicRenderingScope(\n                        recordingState.CommandBuffer,\n                        in scopePlan,\n                        secondaryContents,");
        commandBuffers.ShouldContain("CmdBeginDynamicRendering(\n                commandBuffer,\n                &renderingInfo,\n                preferKhrDynamicRendering);");
        commandBuffers.ShouldContain("TryResolveAttachmentImage(");
    }

    [Test]
    public void DynamicRenderingFormatIdentity_UsesAllocationFreeInlineStorage()
    {
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/DynamicRenderingFormatSignature.cs");

        modeSource.ShouldContain("[InlineArray(MaxColorAttachmentCount)]");
        modeSource.ShouldContain("private readonly ColorFormatStorage _colorFormats;");
        modeSource.ShouldContain("private readonly byte _colorAttachmentCount;");
        modeSource.ShouldContain("ColorFormatStorage storage = default;");
        modeSource.ShouldContain("storage[i] = colorFormats[i];");
        modeSource.ShouldNotContain("ReadOnlySpan<Format>.ToArray()");
        modeSource.ShouldNotContain("colorFormats.ToArray()");
        modeSource.ShouldNotContain("new Format[colorCount]");
        modeSource.ShouldNotContain("Format[]? _colorFormats");
    }

    [Test]
    public void SecondaryInheritanceIdentity_IncludesFlagsAndLocalReadMappings()
    {
        uint[] attachmentLocations = [1u, 0u];
        uint[] inputAttachmentIndices = [0u, 1u];
        DynamicRenderingLocalReadPlan localReadPlan = new(
            attachmentLocations,
            inputAttachmentIndices,
            depthInputAttachmentIndex: 3u);
        DynamicRenderingLocalReadSignature localReadSignature =
            DynamicRenderingLocalReadSignature.Create(
                in localReadPlan);

        localReadSignature.Enabled.ShouldBeTrue();
        localReadSignature.ColorAttachmentLocationCount.ShouldBe(2);
        localReadSignature.ColorInputAttachmentIndexCount.ShouldBe(2);
        uint[] copiedLocations = new uint[2];
        uint[] copiedInputIndices = new uint[2];
        localReadSignature.CopyColorAttachmentLocations(
            copiedLocations);
        localReadSignature.CopyColorInputAttachmentIndices(
            copiedInputIndices);
        copiedLocations.ShouldBe(attachmentLocations);
        copiedInputIndices.ShouldBe(inputAttachmentIndices);

        VulkanRecordedCommandInheritance baseline = new(
            DynamicRendering: true,
            default,
            default,
            default,
            DepthStencilReadOnly: false,
            SampleCountFlags.Count1Bit,
            localReadSignature,
            RenderingFlags: 0);
        VulkanRecordedCommandInheritance changedFlags =
            baseline with
            {
                RenderingFlags = (RenderingFlags)2u,
            };
        DynamicRenderingLocalReadPlan changedLocalReadPlan = new(
            attachmentLocations,
            [1u, 0u],
            depthInputAttachmentIndex: 3u);
        VulkanRecordedCommandInheritance changedLocalRead =
            baseline with
            {
                LocalReadSignature =
                    DynamicRenderingLocalReadSignature.Create(
                        in changedLocalReadPlan),
            };

        changedFlags.ComputeIdentity()
            .ShouldNotBe(baseline.ComputeIdentity());
        changedLocalRead.ComputeIdentity()
            .ShouldNotBe(baseline.ComputeIdentity());
    }

    [Test]
    public void SecondaryRecording_RehydratesAndValidatesCompleteDynamicInheritance()
    {
        string secondaryBuffers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Secondaries.cs");

        secondaryBuffers.ShouldContain(
            "TryAppendDynamicRenderingLocalReadInheritancePNext(");
        secondaryBuffers.ShouldContain(
            "recordingState.RenderScope.LocalReadSignature.Equals(");
        secondaryBuffers.ShouldContain(
            "recordingState.RenderScope.InheritanceRenderingFlags !=");
        secondaryBuffers.ShouldContain(
            "Flags = inheritance.RenderingFlags");
        secondaryBuffers.ShouldContain(
            "in localReadSignature");
    }

    [Test]
    public void DynamicCommandRecording_ClearsMultiviewFramebuffersAsSingleLayerRenderPasses()
    {
        string clearRecording = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Recording/VulkanRenderer.ClearAndPublishRecording.cs");
        string primaryOperations = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs");
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanDynamicRenderingUtilities.cs");
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");

        modeSource.ShouldContain("viewMask == 0u ? Math.Max(framebufferLayers, 1u) : 1u");
        clearRecording.ShouldContain("clearTargetFrameBuffer?.MultiviewViewMask != 0u");
        primaryOperations.ShouldContain("state.RenderScope.DynamicRenderingFormats.ViewMask");
        clearRecording.ShouldContain("ResolveClearRectLayerCount(target, clearTargetFrameBuffer, activeRenderLayerCount, activeRenderViewMask)");
        clearRecording.ShouldContain("if (activeRenderViewMask != 0u || clearTargetFrameBuffer?.MultiviewViewMask != 0u)");
        clearRecording.ShouldContain("activeRenderLayerCount > 1u && IsStereoCompatibleClearTarget(target, clearTargetFrameBuffer)");
        clearRecording.ShouldContain("activeRenderLayerCount > 1u && RuntimeEngine.Rendering.State.IsStereoPass");
        clearRecording.ShouldContain("ClearRect clearRect = new()");
        frameBuffer.ShouldContain("RuntimeEngine.Rendering.State.IsStereoPass");
        frameBuffer.ShouldContain("IsTextureArrayAttachment(texture)");
        frameBuffer.ShouldContain("TryGetTextureArrayMultiviewParameters(texture");
        frameBuffer.ShouldContain("XRTexture2DArrayView { NumLayers: > 1u } textureArrayView => textureArrayView.ViewedTexture.OVRMultiViewParameters");
        frameBuffer.ShouldContain("IsStereoCompatibleTextureArrayAttachment(texture, layerCount)");
        frameBuffer.ShouldContain("descriptor.StereoCompatible && descriptorLayers >= 2u");
        frameBuffer.ShouldContain("BuildMultiviewViewMask(0, 2u, layerCount)");
    }

    [Test]
    public void VulkanFramebuffer_WriteBindingTracksDrawFramebufferState()
    {
        string glFrameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Framebuffers/GLFrameBuffer.cs");
        string vkFrameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string bindFboCommand = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/State/VPRC_BindFBO.cs");

        glFrameBuffer.ShouldContain("Data.BindForWriteRequested += BindForWriting;");
        glFrameBuffer.ShouldContain("Data.BindForWriteRequested -= BindForWriting;");
        glFrameBuffer.ShouldContain("Data.UnbindFromWriteRequested += UnbindFromWriting;");
        glFrameBuffer.ShouldContain("Data.UnbindFromWriteRequested -= UnbindFromWriting;");

        vkFrameBuffer.ShouldContain("Data.BindForWriteRequested += BindForWriting;");
        vkFrameBuffer.ShouldContain("Data.BindForWriteRequested -= BindForWriting;");
        vkFrameBuffer.ShouldContain("Data.UnbindFromWriteRequested += UnbindFromWriting;");
        vkFrameBuffer.ShouldContain("Data.UnbindFromWriteRequested -= UnbindFromWriting;");
        vkFrameBuffer.ShouldContain("CommandOperations.SetBoundFrameBufferState(");
        vkFrameBuffer.ShouldContain("EFramebufferTarget.DrawFramebuffer,\n            Data);");
        vkFrameBuffer.ShouldContain("EFramebufferTarget.DrawFramebuffer,\n            null);");

        bindFboCommand.ShouldContain("FrameBuffer.BindForWriting();");
        bindFboCommand.ShouldContain("PopCommand.Write = true;");
        bindFboCommand.ShouldNotContain("FrameBuffer.Bind();");
    }

    [Test]
    public void VulkanSwapchainOverlayPasses_LoadPresentedImageInsteadOfClearing()
    {
        string commandBuffer = SourceContractWorkspace.ReadVulkanCommandRuntimeSource();

        commandBuffer.ShouldContain("static bool IsOverlayContext(FrameOpContext context)");
        commandBuffer.ShouldContain("context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline");
        commandBuffer.ShouldContain("CountLogicalSwapchainWriter(ref recordingState, context);");
        commandBuffer.ShouldNotContain("sceneSwapchainWriters = swapchainWriteCount;");

        commandBuffer.ShouldContain("(overlaySwapchainPass && recordingState.ImageWasEverPresentedAtRecordStart)");
        commandBuffer.ShouldContain("(legacyOverlaySwapchainPass && recordingState.ImageWasEverPresentedAtRecordStart)");
        commandBuffer.ShouldContain("void ExecuteDynamicUiBatchTextOverlay(scoped ref PrimaryCommandBufferRecordingState recordingState)");
        commandBuffer.ShouldContain("recordingState.ImageWasEverPresentedAtRecordStart = recordingState.SwapchainTarget.ImageEverPresentedAtRecordStart;");
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
        string textureArray = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture2DArray.cs");

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
        gtao.ShouldContain("instance?.RenderState.SceneCamera");
        gtao.ShouldContain("renderState?.SceneCamera as XRCamera");
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
        string logicalDevice = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");
        string featureQueries = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.FeatureQueries.cs");
        string extensions = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceCapabilityReporter.cs");
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/DynamicRenderingLocalReadPlan.cs");
        string scopePlan = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/DynamicRenderingScopePlan.cs");
        string commandBuffers = SourceContractWorkspace.ReadVulkanCommandRuntimeSource();
        string secondaryBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/VulkanRenderer.SecondaryCommandBuffers.cs");
        string sync = ReadWorkspaceFile("XREngine.Runtime.Rendering/RenderGraph/RenderGraphSynchronization.cs");
        string barrierPlanner = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanBarrierUsageMapper.cs");
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");

        extensions.ShouldContain("\"VK_KHR_dynamic_rendering_local_read\"");
        extensions.ShouldContain("SupportsDynamicRenderingLocalRead");
        logicalDevice.ShouldContain("QueryDynamicRenderingLocalReadCapabilities");
        featureQueries.ShouldContain("PhysicalDeviceDynamicRenderingLocalReadFeatures");
        featureQueries.ShouldContain("PhysicalDeviceDynamicRenderingLocalReadFeaturesKHR");
        featureQueries.ShouldContain("PhysicalDeviceVulkan14Properties");
        featureQueries.ShouldContain("DynamicRenderingLocalReadDepthStencilAttachments");
        featureQueries.ShouldContain("DynamicRenderingLocalReadMultisampledAttachments");

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
        scopePlan.ShouldContain("sampleCount,\n            default)");
        scopePlan.ShouldContain("LocalRead = localRead;");
        secondaryBuffers.ShouldContain("TryAppendDynamicRenderingLocalReadPNext");
    }

    [Test]
    public void ModernVulkanCapabilitySnapshot_ReportsMatrixExtensionsAndStrictBackendRequests()
    {
        string logicalDevice = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");
        string capabilityReporter = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceCapabilityReporter.cs");
        string policyValidator = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanExplicitCapabilityPolicyValidator.cs");
        string environment = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string initialization = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs");
        string descriptorHeapBackend = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanRenderer.DescriptorHeap.cs");
        string featureProfile = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Vulkan/VulkanFeatureProfile.cs");

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

        capabilityReporter.ShouldContain("ReportedModernCapabilityExtensionNames");
        capabilityReporter.ShouldContain("foreach (string extensionName in ReportedModernCapabilityExtensionNames)");
        capabilityReporter.ShouldContain("Capability.Extension name={0} available={1} enabled={2}");
        policyValidator.ShouldContain("state=explicitly-required-missing");
        logicalDevice.ShouldContain("TryInitializeDescriptorHeapNativeApi");
        logicalDevice.ShouldContain("QueryDescriptorHeapCapabilities");
        logicalDevice.ShouldContain("PhysicalDeviceDescriptorHeapFeaturesEXTNative");
        logicalDevice.ShouldContain("ResolveDescriptorBackendAfterDeviceCreate");
        logicalDevice.ShouldContain("_activeDescriptorBackend");
        logicalDevice.ShouldContain("VulkanExplicitCapabilityPolicyValidator.Validate(");
        policyValidator.ShouldContain("ThrowExplicitCapabilityMissing");
        policyValidator.ShouldContain("native entry points, feature enablement, or heap storage initialization failed");
        descriptorHeapBackend.ShouldContain("Vulkan.DescriptorHeap.Capability");
        descriptorHeapBackend.ShouldContain("Vulkan.DescriptorHeap.Allocation");
        descriptorHeapBackend.ShouldContain("Vulkan.DescriptorHeap.Active");
        logicalDevice.ShouldContain("ShaderUntypedPointers");
        capabilityReporter.ShouldContain("Capability.Snapshot apiVersion=");
        capabilityReporter.ShouldContain("enabled-active");
        capabilityReporter.ShouldContain("enabled-unused");
        capabilityReporter.ShouldContain("available-disabled");
        capabilityReporter.ShouldContain("unavailable");

        initialization.ShouldContain("_commandRuntime.InitializeSynchronizationBackend(_deviceContext.SupportsSynchronization2);");
        initialization.ShouldContain("VulkanDeviceCapabilityReporter.LogStartupCapabilitySnapshot(");
        initialization.IndexOf("_commandRuntime.InitializeSynchronizationBackend(_deviceContext.SupportsSynchronization2);", StringComparison.Ordinal)
            .ShouldBeLessThan(initialization.IndexOf("VulkanDeviceCapabilityReporter.LogStartupCapabilitySnapshot(", StringComparison.Ordinal));

        string optionalExtensions = SliceArrayInitializer(logicalDevice, "DefaultOptionalDeviceExtensions");
        string reportedExtensions = SliceArrayInitializer(capabilityReporter, "ReportedModernCapabilityExtensionNames");
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
    public void DescriptorHeap_DeclaresNativeInteropMappingPayloadsAndActiveBackend()
    {
        string native = ReadWorkspaceDirectory("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorHeapNative", "*.cs");
        string nativeFunctions = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorHeapNativeFunctions.cs");
        string backend = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanRenderer.DescriptorHeap.cs");
        string bindings = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLifetimeAuthority.cs");
        string logicalDevice = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");
        string commandState = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/State/VulkanRenderer.CommandBufferState.cs");
        string commandBuffers = SourceContractWorkspace.ReadVulkanCommandRuntimeSource();
        string secondaryBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/VulkanRenderer.SecondaryCommandBuffers.cs");
        string program = SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string material = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string meshDescriptors = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorWrites.cs");
        string meshDrawing = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string imgui = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiTextureRegistryService.cs");

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

        nativeFunctions.ShouldContain("vkCmdBindSamplerHeapEXT");
        nativeFunctions.ShouldContain("vkCmdBindResourceHeapEXT");
        nativeFunctions.ShouldContain("vkCmdPushDataEXT");
        nativeFunctions.ShouldContain("vkWriteSamplerDescriptorsEXT");
        nativeFunctions.ShouldContain("vkWriteResourceDescriptorsEXT");
        nativeFunctions.ShouldContain("vkGetPhysicalDeviceDescriptorSizeEXT");
        backend.ShouldContain("CreateDescriptorHeapStorage(\"Sampler\"");
        backend.ShouldContain("CreateDescriptorHeapStorage(\"Resource\"");
        backend.ShouldContain("VulkanDescriptorHeapExt.DescriptorHeapBufferUsage");
        backend.ShouldContain("BufferUsageFlags.ShaderDeviceAddressBit");
        bindings.ShouldContain("TryWriteSamplerDescriptors(");
        bindings.ShouldContain("TryWriteResourceDescriptors(");
        commandBuffers.ShouldContain("TryAppendDescriptorHeapInheritancePNext");
        backend.ShouldContain("_activeDescriptorBackend = EVulkanDescriptorBackend.DescriptorHeap");
        backend.ShouldContain("Descriptor heap is the active descriptor backend.");
        backend.ShouldContain("DeviceLocalWithStaging");
        backend.ShouldContain("Staged heap publication is not implemented.");
        backend.ShouldContain("MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit");
        native.ShouldContain("ResourceHeapReadAccess2");
        backend.ShouldContain("DescriptorHeapLastFrameCopies");

        bindings.ShouldContain("CreateDescriptorHeapProgramLayout");
        bindings.ShouldContain("VulkanDescriptorMappingSourceEXT.HeapWithPushIndex");
        bindings.ShouldContain("TryWriteDescriptorHeapBinding");
        bindings.ShouldContain("TryWriteCombinedImageSamplerHeapPayload");
        bindings.ShouldContain("DescriptorHeapPushDataPayload");
        bindings.ShouldContain("TryWriteSamplerDescriptors(");
        bindings.ShouldContain("TryWriteResourceDescriptors(");

        logicalDevice.ShouldContain("descriptorHeapDependenciesReady");
        logicalDevice.ShouldContain("shaderUntypedPointersExtensionAvailable");
        logicalDevice.ShouldContain("descriptorHeapFeatureEnable");
        logicalDevice.ShouldContain("ResolveDescriptorBackendAfterDeviceCreate");
        commandState.ShouldContain("PrimaryCommandEncoder.TryPushDescriptorHeapData(");
        commandState.ShouldContain("InvalidateDescriptorHeapBindingState");
        commandState.ShouldContain("InvalidateDescriptorSetBindingState");
        commandBuffers.ShouldContain("TryAppendDescriptorHeapInheritancePNext");
        commandBuffers.ShouldContain("TryBuildAndBindComputeDescriptorSets");
        secondaryBuffers.ShouldContain("TryAppendDescriptorHeapInheritancePNext");
        logicalDevice.ShouldContain("_activeDescriptorBackend = EVulkanDescriptorBackend.DescriptorIndexing;");
        program.ShouldContain("CreateDescriptorHeapProgramLayout");
        program.ShouldContain("ShaderDescriptorSetAndBindingMappingInfoEXTNative");
        program.ShouldContain("PipelineCreate2DescriptorHeapBit");
        meshDrawing.ShouldContain("encoder.TryPushDescriptorHeapProgramData(");
        material.ShouldContain("DescriptorHeapPushData");
        material.ShouldContain("TryWriteDescriptorHeapBinding");
        meshDescriptors.ShouldContain("TryWriteDescriptorHeapBinding");
        meshDrawing.ShouldContain("TryPushDescriptorHeapProgramData");
        imgui.ShouldContain("TryWriteCombinedImageSamplerHeapPayload");
        imgui.ShouldContain("ResolveImGuiDescriptorHeapPayload");
    }

    [Test]
    public void DynamicRenderingResolveAttachments_AreMappedToNativeResolveFieldsAndValidated()
    {
        string usage = ReadWorkspaceFile("XREngine.Runtime.Rendering/RenderGraph/RenderPassResourceUsage.cs");
        string builder = ReadWorkspaceFile("XREngine.Runtime.Rendering/RenderGraph/RenderPassBuilder.cs");
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/DynamicRenderingAttachmentPlan.cs");
        string commandBuffers = SourceContractWorkspace.ReadVulkanCommandRuntimeSource();
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string renderPasses = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanFrameBufferResourceService.cs");

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
        renderPasses.ShouldContain("PResolveAttachments = resolveRefs.Length == 0 ? null : resolveRefsPtr");
        renderPasses.ShouldContain("Attachment = uint.MaxValue");
    }

    [Test]
    public void BarrierPlanner_TracksSwapchainPseudoResourceWithoutPhysicalImageGroup()
    {
        string planner = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanBarrierPlanner.cs");
        string primarySetup = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Setup.cs");
        string passBarriers = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.OverlayAndBarriers.cs");
        string swapchain = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Swapchain.cs");

        planner.ShouldContain("private readonly List<PlannedSwapchainBarrier> _swapchainBarriers");
        planner.ShouldContain("public IReadOnlyList<PlannedSwapchainBarrier> GetSwapchainBarriersForPass");
        planner.ShouldContain("IsSwapchainTargetUsage(usage, resourcePlanner)");
        planner.ShouldContain("TrackSwapchainUsage(pass, usage, edge, ownership)");
        planner.ShouldContain("PlannedImageState.FromSwapchainUsage");
        planner.ShouldContain("PlannedImageState.SwapchainPresentInitial()");
        planner.ShouldContain("internal readonly record struct PlannedSwapchainBarrier");
        planner.ShouldContain("ResourceName.Equals(RenderGraphResourceNames.OutputRenderTarget");
        planner.ShouldContain("yield break; // swapchain target handled separately");

        primarySetup.ShouldContain("barrierPlan.GetSwapchainBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex)");
        primarySetup.ShouldContain("EmitPlannedSwapchainBarriers(ref recordingState, recordingState.CommandBuffer, plannedSwapchainBarriers)");
        passBarriers.ShouldContain("barrierPlan.GetSwapchainBarriersForPass(passIndex)");
        passBarriers.ShouldContain("EmitPlannedSwapchainBarriers(ref recordingState, recordingState.CommandBuffer, swapchainBarriers)");
        swapchain.ShouldContain("ImageLayout liveOldLayout = ResolveCurrentSwapchainColorLayout(ref recordingState)");
    }

    [Test]
    public void TransientAttachmentPolicy_TracksAttachmentOnlyCandidatesWithoutActivatingLazyAllocation()
    {
        string policy = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanTransientAttachmentPolicy.cs");
        string request = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanAllocationRequest.cs");
        string plan = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanTransientAttachmentPlan.cs");

        policy.ShouldContain("PreferLazilyAllocated");
        request.ShouldContain("requiresPersistentShaderOrTransferAccess");
        request.ShouldContain("RenderPipelineResourceUsage.SampledTexture");
        request.ShouldContain("RenderPipelineResourceUsage.StorageImage");
        request.ShouldContain("RenderPipelineResourceUsage.TransferSource");
        request.ShouldContain("RenderPipelineResourceUsage.TransferDestination");
        request.ShouldContain("return isAttachment && !requiresPersistentShaderOrTransferAccess");
        plan.ShouldContain("lifetime.GraphicsQueueOnly");
        plan.ShouldContain("lifetime.AttachmentOnly");
        plan.ShouldContain("candidateLazyAllocationCount++;");
        plan.ShouldContain("internal bool IsActive => false;");
    }

    [Test]
    public void DynamicRenderingAttachmentTransitions_UseLayoutCompatibleStageAccessMasks()
    {
        string commandBuffers = SourceContractWorkspace.ReadVulkanCommandRuntimeSource();

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
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string blit = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.BarrierServices.cs");

        frameBuffer.ShouldContain("ResolveAttachmentRole(attachment, source.AspectMask, source.Format)");
        frameBuffer.ShouldContain("NormalizeAttachmentAspectMask(source.DescriptorFormat, source.DescriptorAspect)");
        frameBuffer.ShouldContain("VkFormatConversions.IsDepthStencilFormat(source.DescriptorFormat)");
        frameBuffer.ShouldContain("RenderGraphImageLayout.ColorAttachment => AttachmentRoleClassifier.IsColorLike(signature.Role)");
        frameBuffer.ShouldContain(": ImageLayout.DepthStencilAttachmentOptimal");
        blit.ShouldContain("or Format.S8Uint");
        blit.ShouldContain("if (!IsDepthOrStencilFormat(format))");
        blit.ShouldContain("Format.S8Uint => ImageAspectFlags.StencilBit");
    }

    [Test]
    public void RetiredImageResources_AreDeduplicatedBeforeDestroy()
    {
        string queue = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Retirement/VulkanResourceRetirementQueue.cs");
        string images = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Images/VulkanImageResourceService.cs");
        string runtime = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");

        queue.ShouldContain("HashSet<ulong>[] ImageHandles");
        queue.ShouldContain("HashSet<ulong> AllImageHandles");
        queue.ShouldContain("HashSet<ulong>[] ImageMemoryHandles");
        queue.ShouldContain("HashSet<VulkanPinnedResourceGeneration>[] ImageViewHandles");
        queue.ShouldContain("HashSet<ulong>[] SamplerHandles");
        images.ShouldContain("FilterRetiredAttachmentViews(");
        images.ShouldContain("lifetime.Retirement.AllImageHandles.Add(image.Handle)");
        images.ShouldContain("lifetime.Retirement.AllImageMemoryHandles.Add(memory.Handle)");
        images.ShouldContain("lifetime.Retirement.AllSamplerHandles.Add(sampler.Handle)");
        runtime.ShouldContain("CanDestroyResourceGeneration(");
        runtime.ShouldContain("entry.ImageGeneration");
        runtime.ShouldContain("entry.SamplerGeneration");
        runtime.ShouldContain("Allocations.Images.Allocations.TryRemove(");
        runtime.ShouldContain("Allocations.Buffers.MemoryAllocator!.Free(");
        runtime.ShouldContain("CompleteRetiredImageDeduplication(frameSlot, in entry)");
        runtime.ShouldContain("Lifetime.Retirement.AllImageHandles.Remove(");
        runtime.ShouldNotContain("api.FreeMemory(device, resources.Memory, null)");
    }

    [Test]
    public void DynamicPipelines_AreKeyedByAttachmentFormatSignatureWithoutRenderPassHandles()
    {
        string pipelineKey = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Pipelines/VkMeshRenderer.PipelineKey.cs");
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string prewarm = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderer.PipelinePrewarmDatabase.cs");
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/DynamicRenderingFormatSignature.cs");

        pipelineKey.ShouldContain("DynamicRenderingFormatSignature DynamicRenderingFormats");
        meshPipeline.ShouldContain("useDynamicRendering ? 0UL : renderPass.Handle");
        meshPipeline.ShouldContain("dynamicRenderingFormats.GetColorAttachmentFormat");
        string pipelineFactory = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanGraphicsPipelineFactory.cs");
        pipelineFactory.ShouldContain("request.DynamicRenderingFormats.CopyColorAttachmentFormats");
        pipelineFactory.ShouldContain("DepthAttachmentFormat = request.DynamicRenderingFormats.DepthAttachmentFormat");
        pipelineFactory.ShouldContain("StencilAttachmentFormat = request.DynamicRenderingFormats.StencilAttachmentFormat");
        prewarm.ShouldContain("BuildDynamicRenderingSignature(dynamicRenderingFormats)");
        prewarm.ShouldContain("dynamicRenderingFormats.DescribeColorFormats()");
        modeSource.ShouldContain("DescribeColorFormats()");
    }

    [Test]
    public void GraphicsPipelineLibraryExtension_EnablesRequiredKhrDependency()
    {
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");

        logicalDeviceSource.ShouldContain("\"VK_KHR_pipeline_library\"");
        logicalDeviceSource.ShouldContain("\"VK_EXT_graphics_pipeline_library\"");
        logicalDeviceSource.ShouldContain("optionalExt == \"VK_EXT_graphics_pipeline_library\"");
        logicalDeviceSource.ShouldContain("!availableExtensionSet.Contains(\"VK_KHR_pipeline_library\")");
        logicalDeviceSource.ShouldContain("graphicsPipelineLibraryDependencyEnabled");
        logicalDeviceSource.ShouldContain("extensionsArray.Contains(\"VK_KHR_pipeline_library\")");
    }

    [Test]
    public void GraphicsPipelineLibraryKeys_AreSubsetScopedAndPendingLinksAreNotLoggedAsFailures()
    {
        string graphicsLibraryKey = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Pipelines/VkMeshRenderer.GraphicsPipelineLibraryKey.cs");
        string pipelineFactory = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanGraphicsPipelineFactory.cs");
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

        graphicsLibraryKey.ShouldContain("internal readonly record struct VulkanGraphicsPipelineLibraryKey(");
        graphicsLibraryKey.ShouldContain("VulkanGraphicsPipelineLibrarySubset Subset,");
        graphicsLibraryKey.ShouldContain("DynamicRenderingFormatSignature DynamicRenderingFormats,");
        pipelineFactory.ShouldContain("CreateGraphicsPipelineLibraryKey(VulkanGraphicsPipelineLibrarySubset.VertexInputInterface, request.Key, request.UsesDescriptorHeap)");
        pipelineFactory.ShouldContain("hasProgram = subset is VulkanGraphicsPipelineLibrarySubset.PreRasterizationShaders or VulkanGraphicsPipelineLibrarySubset.FragmentShader");
        pipelineFactory.ShouldContain("hasDepthStencil = subset is VulkanGraphicsPipelineLibrarySubset.FragmentShader or VulkanGraphicsPipelineLibrarySubset.FragmentOutputInterface");
        pipelineFactory.ShouldContain("hasBlendState = subset == VulkanGraphicsPipelineLibrarySubset.FragmentOutputInterface");
        pipelineFactory.ShouldContain("DynamicRenderingFormatSignature dynamicRenderingFormats = CreateGraphicsPipelineLibraryDynamicRenderingFormatSignature(subset, pipeline);");
        pipelineFactory.ShouldContain("CreateGraphicsPipelineLibraryDynamicRenderingFormatSignature(");
        pipelineFactory.ShouldContain("pipeline.DynamicRenderingFormats.ViewMask");
        pipelineFactory.ShouldContain("VulkanGraphicsPipelineLibrarySubset.FragmentOutputInterface => pipeline.DynamicRenderingFormats");
        pipelineFactory.ShouldContain("bool includeDynamicRenderingInfo = key.UseDynamicRendering;");
        pipelineFactory.ShouldContain("PipelineRenderingCreateInfo libraryRenderingInfo = default;");
        pipelineFactory.ShouldContain("PNext = includeDynamicRenderingInfo ? &libraryRenderingInfo : null");
        pipelineFactory.ShouldContain("ApplyGraphicsPipelineLibrarySubset(ref libraryPipelineInfo, key.Subset)");
        pipelineFactory.ShouldContain("linkedRenderingInfo.PNext = &libraryInfo;");
        pipelineFactory.ShouldContain("linkedInfo.PNext = &linkedRenderingInfo;");
        pipelineFactory.ShouldContain("void* originalPipelinePNext = pipelineInfo.PNext;");
        pipelineFactory.ShouldContain("PNext = originalPipelinePNext,");
        pipelineFactory.ShouldContain("pipelineInfo.PNext = originalPipelinePNext;");
        pipelineFactory.ShouldContain("case VulkanGraphicsPipelineLibrarySubset.PreRasterizationShaders:");
        pipelineFactory.ShouldContain("pipelineInfo.PDepthStencilState = null;");
        pipelineFactory.ShouldContain("pipelineInfo.PColorBlendState = null;");
        pipelineFactory.ShouldNotContain("linkedInfo.PDepthStencilState = null;");
        pipelineFactory.ShouldNotContain("linkedInfo.PColorBlendState = null;");

        meshPipeline.ShouldContain("XRRenderProgram.ShaderProgramBackendStatus backend = _program.Data.ShaderMetadata.Backend");
        meshPipeline.ShouldContain("backend.Stage == XRRenderProgram.EShaderProgramBackendStage.Failed");
        meshPipeline.ShouldContain("program link failed");
    }

    [Test]
    public void DynamicRenderingDepthOnlyPasses_CreatePipelinesInsteadOfSkippingDraws()
    {
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string pipelineFactory = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanGraphicsPipelineFactory.cs");

        meshPipeline.ShouldContain("ResolveAttachmentCompatibleDrawState(");
        meshPipeline.ShouldContain("colorAttachmentCount == 0");
        meshPipeline.ShouldContain("ColorWriteMask = 0");
        meshPipeline.ShouldContain("BlendEnabled = false");
        meshPipeline.ShouldContain("AlphaToCoverageEnabled = false");
        meshPipeline.ShouldContain("PipelineColorBlendAttachmentState[] blendAttachments = colorAttachmentCount == 0");
        pipelineFactory.ShouldContain("request.Key.UseDynamicRendering && request.ColorAttachmentCount == 0");
        pipelineFactory.ShouldContain("Vulkan.PipelineLibrary.DepthOnlyMonolithic");
        pipelineFactory.ShouldContain("graphics pipeline libraries are bypassed for zero-color pipelines");
        pipelineFactory.ShouldContain("return CreateMonolithicGraphicsPipeline(manager, request, ref pipelineInfo, pipelineCache, backgroundCompile);");
        meshPipeline.ShouldNotContain("Vulkan.MeshRenderer.SkipDraw.NoColorAttachment");
        meshPipeline.ShouldNotContain("dynamic rendering has undefined color attachment format while color writes are enabled");
    }

    [Test]
    public void GeneratedProgramIdentity_IncludesShaderAndMaterialStateAxes()
    {
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

        meshPipeline.ShouldContain("BuildGeneratedProgramIdentity(programState, generatedProgramAxes, shaderStageList, generatedVertexIdentity)");
        meshPipeline.ShouldContain("uberVariant={state.MaterialVariantHash:X16}");
        meshPipeline.ShouldContain("shaderSignature={state.ShaderSourceSignature:X16}");
        meshPipeline.ShouldContain("shaderRevision={state.ShaderStateRevision}");
        meshPipeline.ShouldContain("generatedVertex={generatedVertexIdentity ?? string.Empty}");
    }

    [Test]
    public void SynchronousDepthReadback_UsesBoundFramebufferBeforeSwapchainFallback()
    {
        string readback = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Readback.cs");
        string blit = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Readback/VulkanCommandRuntime.PixelReadback.cs");

        int getDepthIndex = readback.IndexOf("internal float GetDepth(int x, int y)", StringComparison.Ordinal);
        int boundFramebufferIndex = readback.IndexOf("boundReadFrameBuffer is not null", getDepthIndex, StringComparison.Ordinal);
        int swapchainFallbackIndex = readback.IndexOf("TryReadSwapchainDepthPixel", getDepthIndex, StringComparison.Ordinal);
        int depthReadIndex = blit.IndexOf("internal bool TryReadDepthPixel", StringComparison.Ordinal);
        int liveDepthIndex = blit.IndexOf("TryResolveLiveBlitImage(source, out BlitImageInfo liveSource)", depthReadIndex, StringComparison.Ordinal);
        int liveDepthCopyIndex = blit.IndexOf("liveSource.Image", liveDepthIndex, StringComparison.Ordinal);

        getDepthIndex.ShouldBeGreaterThanOrEqualTo(0);
        boundFramebufferIndex.ShouldBeGreaterThan(getDepthIndex);
        swapchainFallbackIndex.ShouldBeGreaterThan(boundFramebufferIndex);
        readback.ShouldContain("TryResolveBlitImage(");
        readback.ShouldContain("wantDepth: true");
        readback.ShouldContain("_commandRuntime.TryReadDepthPixel(depthSource, x, y, out float fboDepth)");
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
        editorPawn.ShouldContain("RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend");
        editorPawn.ShouldContain("RenderClipSpacePolicy.FramebufferTextureYDirection(backend)");
        editorPawn.ShouldContain("int maxY = Math.Max((int)fbo.Height - 1, 0);");
        editorPawn.ShouldContain("coordinate.Y = maxY - coordinate.Y;");
    }

    [Test]
    public void CommonPushConstants_AreVisibleToGeometryShaders()
    {
        string conventions = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanMeshRenderingConventions.cs");
        string commandBuffers = SourceContractWorkspace.ReadVulkanCommandRuntimeSource();
        string renderProgram = SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string programPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgramPipeline.cs");
        string meshDrawing = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

        conventions.ShouldContain("internal const ShaderStageFlags CommonPushConstantStageFlags");
        conventions.ShouldContain("ShaderStageFlags.GeometryBit |");
        conventions.ShouldContain("ShaderStageFlags.TessellationEvaluationBit |");
        commandBuffers.ShouldContain("VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(");
        renderProgram.ShouldContain("StageFlags = VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(");
        programPipeline.ShouldContain("StageFlags = VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(");
        meshDrawing.ShouldContain("VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(");
    }

    [Test]
    public void FboDepthStencilMetadata_PreservesStencilForOnTopAndPostProcessPasses()
    {
        string viewportCommand = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/ViewportRenderCommand.cs");
        string quadBlit = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderQuadToFBO.Internal.cs");
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
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
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");

        string passUsesReadOnly = SliceMethod(meshPipeline, "private static bool PassHasReadOnlyDepthStencilUsage(");
        passUsesReadOnly.ShouldContain("bool hasDepthStencilWriteUsage = false;");
        passUsesReadOnly.ShouldContain("usage.Access is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite");
        passUsesReadOnly.ShouldContain("return hasDepthStencilUsage && !hasDepthStencilWriteUsage;");

        frameBuffer.ShouldContain("bool[] writeCapableDepthStencilAttachments = new bool[planned.Length];");
        frameBuffer.ShouldContain("ResolveAttachmentReferenceLayout(updated, usage, writeCapableDepthStencilAttachments[index])");

        string collectWrites = SliceMethod(frameBuffer, "private static void CollectWriteCapableDepthStencilAttachments(");
        collectWrites.ShouldContain("usage.Access == ERenderGraphAccess.Read");
        collectWrites.ShouldContain("ResolveMatchingAttachmentIndices(signatures, slot, usage, pass, matchingIndices)");

        string referenceLayout = SliceMethod(frameBuffer, "private static ImageLayout ResolveAttachmentReferenceLayout(");
        referenceLayout.ShouldContain("usage.Access == ERenderGraphAccess.Read && !passHasWriteCapableDepthStencilUsage");
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

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
