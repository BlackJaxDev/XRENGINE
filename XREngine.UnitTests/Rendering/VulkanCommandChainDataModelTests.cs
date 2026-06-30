using System.Collections.Generic;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCommandChainDataModelTests
{
    [Test]
    public void RenderViewKey_EqualityAndHash_AreStable()
    {
        RenderViewKey a = new(
            PipelineIdentity: 10,
            ViewportIdentity: 20,
            ViewIndex: 1,
            Kind: RenderViewKind.VREye,
            LightIdentity: 30,
            CascadeIndex: 2);
        RenderViewKey b = new(
            PipelineIdentity: 10,
            ViewportIdentity: 20,
            ViewIndex: 1,
            Kind: RenderViewKind.VREye,
            LightIdentity: 30,
            CascadeIndex: 2);
        RenderViewKey differentKind = a with { Kind = RenderViewKind.Shadow };

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        differentKind.ShouldNotBe(a);
    }

    [Test]
    public void BuildRenderViewKey_UsesExplicitStereoEyeIndices()
    {
        XRCamera leftCamera = new(new Transform(), new XROVRCameraParameters(true, 0.1f, 1000.0f));
        XRCamera rightCamera = new(new Transform(), new XROVRCameraParameters(false, 0.1f, 1000.0f));
        VulkanRenderer.MeshDrawOp leftOp = CreateMeshDrawOp(default(VulkanRenderer.PendingMeshDraw) with { Camera = leftCamera });
        VulkanRenderer.MeshDrawOp rightOp = CreateMeshDrawOp(default(VulkanRenderer.PendingMeshDraw) with { Camera = rightCamera });

        RenderViewKey leftKey = VulkanRenderer.BuildRenderViewKey(leftOp, dynamicOverlay: false);
        RenderViewKey rightKey = VulkanRenderer.BuildRenderViewKey(rightOp, dynamicOverlay: false);

        leftKey.Kind.ShouldBe(RenderViewKind.VREye);
        leftKey.ViewIndex.ShouldBe(VulkanRenderer.CommandChainLeftEyeViewIndex);
        rightKey.Kind.ShouldBe(RenderViewKind.VREye);
        rightKey.ViewIndex.ShouldBe(VulkanRenderer.CommandChainRightEyeViewIndex);
        leftKey.ShouldNotBe(rightKey);
    }

    [Test]
    public void BuildRenderViewKey_SinglePassStereoUsesMultiviewSentinel()
    {
        VulkanRenderer.MeshDrawOp op = CreateMeshDrawOp(default(VulkanRenderer.PendingMeshDraw) with { IsStereoPass = true });

        RenderViewKey key = VulkanRenderer.BuildRenderViewKey(op, dynamicOverlay: false);

        key.Kind.ShouldBe(RenderViewKind.VREye);
        key.ViewIndex.ShouldBe(VulkanRenderer.CommandChainStereoMultiviewViewIndex);
    }

    [Test]
    public void BuildRenderViewKey_SinglePassStereoPrefersMultiviewSentinelOverEyeCamera()
    {
        XRCamera leftCamera = new(new Transform(), new XROVRCameraParameters(true, 0.1f, 1000.0f));
        VulkanRenderer.MeshDrawOp op = CreateMeshDrawOp(default(VulkanRenderer.PendingMeshDraw) with
        {
            Camera = leftCamera,
            IsStereoPass = true
        });

        RenderViewKey key = VulkanRenderer.BuildRenderViewKey(op, dynamicOverlay: false);

        key.Kind.ShouldBe(RenderViewKind.VREye);
        key.ViewIndex.ShouldBe(VulkanRenderer.CommandChainStereoMultiviewViewIndex);
    }

    [Test]
    public void OpenXrEyeRenderTargetContext_SeparatesLeftAndRightTargetIdentity()
    {
        Extent2D extent = new(2160, 2160);
        VulkanRenderer.OpenXrEyeRenderTargetContext left = new(
            OpenXrViewIndex: 0u,
            OpenXrImageIndex: 4u,
            Image: new Image(0x1001UL),
            ImageView: new ImageView(0x1002UL),
            ImageFormat: Format.B8G8R8A8Srgb,
            Extent: extent,
            DepthImage: new Image(0x1003UL),
            DepthMemory: new DeviceMemory(0x1004UL),
            DepthView: new ImageView(0x1005UL),
            DepthFormat: Format.D32Sfloat,
            DepthAspect: ImageAspectFlags.DepthBit,
            ExternalTargetRegion: new BoundingRectangle(0, 0, 2160, 2160),
            CommandChainImageKey: 1_000_010u,
            FrameDataSlotIndex: 3u,
            ResourcePlannerStateIndex: 0,
            FoveationResourceKey: 0xF001UL,
            FoveationAttachmentKind: EVrFoveationAttachmentKind.VulkanFragmentShadingRate,
            FoveationAttachmentOwnedByResourcePlanner: true);
        VulkanRenderer.OpenXrEyeRenderTargetContext right = new(
            OpenXrViewIndex: 1u,
            OpenXrImageIndex: 4u,
            Image: new Image(0x2001UL),
            ImageView: new ImageView(0x2002UL),
            ImageFormat: Format.B8G8R8A8Srgb,
            Extent: extent,
            DepthImage: new Image(0x2003UL),
            DepthMemory: new DeviceMemory(0x2004UL),
            DepthView: new ImageView(0x2005UL),
            DepthFormat: Format.D32Sfloat,
            DepthAspect: ImageAspectFlags.DepthBit,
            ExternalTargetRegion: new BoundingRectangle(0, 0, 2160, 2160),
            CommandChainImageKey: 1_000_020u,
            FrameDataSlotIndex: 4u,
            ResourcePlannerStateIndex: 1,
            FoveationResourceKey: 0xF002UL,
            FoveationAttachmentKind: EVrFoveationAttachmentKind.VulkanFragmentDensityMap,
            FoveationAttachmentOwnedByResourcePlanner: true);

        left.IsValid.ShouldBeTrue();
        right.IsValid.ShouldBeTrue();
        left.Image.ShouldNotBe(right.Image);
        left.ImageView.ShouldNotBe(right.ImageView);
        left.DepthImage.ShouldNotBe(right.DepthImage);
        left.DepthView.ShouldNotBe(right.DepthView);
        left.CommandChainImageKey.ShouldNotBe(right.CommandChainImageKey);
        left.FrameDataSlotIndex.ShouldNotBe(right.FrameDataSlotIndex);
        left.ResourcePlannerStateIndex.ShouldNotBe(right.ResourcePlannerStateIndex);
        left.FoveationResourceKey.ShouldNotBe(right.FoveationResourceKey);
        left.FoveationAttachmentKind.ShouldNotBe(right.FoveationAttachmentKind);
        VulkanRenderer.OpenXrViewResourcePlannerContextKey.FromTarget(left)
            .ShouldNotBe(VulkanRenderer.OpenXrViewResourcePlannerContextKey.FromTarget(right));

        ulong leftKey = VulkanRenderer.BuildOpenXrPrimaryCommandBufferCacheKey(left.CommandChainImageKey, left);
        ulong rightKey = VulkanRenderer.BuildOpenXrPrimaryCommandBufferCacheKey(right.CommandChainImageKey, right);
        leftKey.ShouldNotBe(rightKey);
    }

    [Test]
    public void OpenXrEyePrimaryRecording_PassesTargetContextIntoCommandBufferRecording()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        commandBufferSource.ShouldContain("OpenXrEyeRenderTargetContext? openXrTargetContext = null");
        commandBufferSource.ShouldContain("SwapchainRecordingTarget swapchainTarget = ResolveSwapchainRecordingTarget(imageIndex, openXrTargetContext);");
        commandBufferSource.ShouldContain("CreateSwapchainDynamicRenderingFormatSignature(swapchainTarget.ImageFormat, swapchainTarget.DepthFormat)");
        commandBufferSource.ShouldContain("Image = swapchainTarget.Image");
        commandBufferSource.ShouldContain("ImageView = swapchainTarget.ImageView");
        commandBufferSource.ShouldContain("ImageView = swapchainTarget.DepthView");
        openXrSource.ShouldContain("openXrTargetContext: targetContext");
        openXrSource.ShouldNotContain("ApplyOpenXrEyeRenderTargetContext");
    }

    [Test]
    public void OpenXrResourcePlannerState_IsKeyedByViewTargetAndFoveationContext()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");

        openXrSource.ShouldContain("private readonly Dictionary<OpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState> _openXrResourcePlannerStates = new();");
        openXrSource.ShouldContain("private readonly object _openXrResourcePlannerStatesLock = new();");
        openXrSource.ShouldContain("EnterOpenXrResourcePlannerThreadScope(OpenXrViewResourcePlannerContextKey.FromTarget(in targetContext))");
        openXrSource.ShouldContain("private OpenXrResourcePlannerThreadScope EnterOpenXrResourcePlannerThreadScope(int stateIndex)");
        openXrSource.ShouldContain("target.FoveationResourceKey");
        openXrSource.ShouldContain("target.FoveationAttachmentKind");
        openXrSource.ShouldContain("target.FoveationAttachmentOwnedByResourcePlanner");
        openXrSource.ShouldContain("DescribeOpenXrResourcePlannerContextKey");
        openXrSource.ShouldContain("_openXrResourcePlannerStates.TryGetValue(_contextKey");
        openXrSource.ShouldContain("_openXrResourcePlannerStates[_contextKey] = state;");
        openXrSource.ShouldNotContain("EnterOpenXrResourcePlannerScope");
        openXrSource.ShouldNotContain("OpenXrResourcePlannerScope");
        openXrSource.ShouldNotContain("renderer.RestoreResourcePlannerRuntimeState(openXrState)");
        openXrSource.ShouldNotContain("private readonly ResourcePlannerRuntimeState[] _openXrResourcePlannerStates");
        openXrSource.ShouldNotContain("_hasOpenXrResourcePlannerStates");
    }

    [Test]
    public void FrameOpResourcePlannerSwitchingState_IsScopedWithOpenXrThreadPlannerContext()
    {
        string stateTrackingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string resourcePlannerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string commandChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandChainLowering.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        stateTrackingSource.ShouldContain("private sealed class FrameOpResourcePlannerSwitchingState");
        stateTrackingSource.ShouldContain("private static VulkanRenderer? _threadFrameOpResourcePlannerSwitchingStateOwner;");
        stateTrackingSource.ShouldContain("private static FrameOpResourcePlannerSwitchingState? _threadFrameOpResourcePlannerSwitchingState;");
        stateTrackingSource.ShouldContain("private FrameOpResourcePlannerSwitchingState ActiveFrameOpResourcePlannerSwitchingState");
        stateTrackingSource.ShouldContain("EnterThreadFrameOpResourcePlannerSwitchingStateScope");
        stateTrackingSource.ShouldContain("state.FrameOpResourcePlannerSwitchingState = ActiveFrameOpResourcePlannerSwitchingState;");
        openXrSource.ShouldContain("private readonly ThreadFrameOpResourcePlannerSwitchingStateScope _frameOpThreadScope;");
        openXrSource.ShouldContain("openXrState.FrameOpResourcePlannerSwitchingState ??= new FrameOpResourcePlannerSwitchingState();");
        openXrSource.ShouldContain("state.FrameOpResourcePlannerSwitchingState = _frameOpThreadScope.CaptureCurrent(_renderer);");
        resourcePlannerSource.ShouldContain("FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;");
        commandChainSource.ShouldContain("FrameOpResourcePlannerSwitchingState frameOpSwitchingState = ActiveFrameOpResourcePlannerSwitchingState;");
        commandBufferSource.ShouldContain("if (ActiveFrameOpResourcePlannerSwitchingState.SwitchingActive)");
        stateTrackingSource.ShouldNotContain("private bool _frameOpResourcePlannerSwitchingActive;");
        stateTrackingSource.ShouldNotContain("private bool _frameOpResourcePlannerRecordingScopeActive;");
        stateTrackingSource.ShouldNotContain("private bool _hasActiveFrameOpResourcePlannerStateKey;");
        stateTrackingSource.ShouldNotContain("private FrameOpPlannerStateKey _activeFrameOpResourcePlannerStateKey;");
    }

    [Test]
    public void OpenXrExternalTargetAndUploadBlockState_AreThreadScopedForEyeWorkers()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string externalScopeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXrExternalSwapchainRenderScope.cs");
        string uploadBlockScopeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.SynchronousResourceUploadBlockScope.cs");

        openXrSource.ShouldContain("[ThreadStatic]");
        openXrSource.ShouldContain("private static VulkanRenderer? _threadOpenXrExternalSwapchainRenderer;");
        openXrSource.ShouldContain("IsThreadOpenXrExternalSwapchainTarget ||");
        openXrSource.ShouldContain("private bool IsThreadOpenXrExternalSwapchainTarget");
        openXrSource.ShouldContain("_threadOpenXrExternalSwapchainTargetRegion");
        openXrSource.ShouldContain("using IDisposable externalScope = EnterOpenXrExternalSwapchainRenderScope(");
        openXrSource.ShouldNotContain("_openXrExternalSwapchainRenderDepth++;");
        openXrSource.ShouldNotContain("_openXrExternalSwapchainRenderDepth--;");

        externalScopeSource.ShouldContain("_threadOpenXrExternalSwapchainRenderer = renderer;");
        externalScopeSource.ShouldContain("_threadOpenXrExternalSwapchainTargetRegion = region;");
        externalScopeSource.ShouldContain("Interlocked.Increment(ref renderer._openXrExternalSwapchainRenderDepth);");
        externalScopeSource.ShouldContain("Interlocked.Decrement(ref _renderer._openXrExternalSwapchainRenderDepth)");

        openXrSource.ShouldContain("private static VulkanRenderer? _threadSynchronousResourceUploadBlockRenderer;");
        openXrSource.ShouldContain("private bool IsThreadSynchronousResourceUploadBlocked");
        openXrSource.ShouldContain("=> !IsThreadSynchronousResourceUploadBlocked &&");
        uploadBlockScopeSource.ShouldContain("_threadSynchronousResourceUploadBlockRenderer = renderer;");
        uploadBlockScopeSource.ShouldContain("Interlocked.Increment(ref renderer._synchronousResourceUploadBlockDepth);");
        uploadBlockScopeSource.ShouldContain("Interlocked.Decrement(ref _renderer._synchronousResourceUploadBlockDepth)");
    }

    [Test]
    public void AbstractRendererCurrent_IsThreadScopedForOpenXrEyeWorkers()
    {
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs");
        string workerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.EyeRecordWorkers.cs");

        rendererSource.ShouldContain("[ThreadStatic]\n        private static AbstractRenderer? _threadCurrent;");
        rendererSource.ShouldContain("[ThreadStatic]\n        private static bool _hasThreadCurrentOverride;");
        rendererSource.ShouldContain("private static AbstractRenderer? _globalCurrent;");
        rendererSource.ShouldContain("get => _hasThreadCurrentOverride ? _threadCurrent : _globalCurrent;");
        rendererSource.ShouldContain("internal static IDisposable PushThreadCurrent(AbstractRenderer? renderer)");
        rendererSource.ShouldContain("private readonly struct ThreadCurrentScope : IDisposable");
        workerSource.ShouldContain("using IDisposable currentRendererScope = AbstractRenderer.PushThreadCurrent(this);");
        workerSource.ShouldContain("TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in prepared, out recorded)");
    }

    [Test]
    public void OpenXrEyePrimaryCommandBuffers_UseEyeOwnedCommandPools()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferState.cs");

        stateSource.ShouldContain("public CommandPool PrimaryCommandPool { get; }");
        stateSource.ShouldContain("public CommandPool DynamicUiSecondaryCommandPool { get; }");
        openXrSource.ShouldContain("private readonly CommandPool[] _openXrEyeCommandPools = new CommandPool[OpenXrEyeResourcePlannerStateCount];");
        openXrSource.ShouldContain("GetOrCreateOpenXrEyeCommandPool(targetContext.OpenXrViewIndex)");
        openXrSource.ShouldContain("OpenXR eye primary command buffer variant eye=");
        openXrSource.ShouldContain("DestroyOpenXrEyeCommandPools();");
        openXrSource.ShouldContain("variant.PrimaryCommandPool.Handle != 0");
        openXrSource.ShouldContain("Api!.FreeCommandBuffers(device, ownerPool, 1, ref primary);");
    }

    [Test]
    public void OpenXrPrimaryCommandBufferCache_AccessIsLockedWithoutLockingWholeRecordPath()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");

        openXrSource.ShouldContain("private readonly object _openXrPrimaryCommandBufferVariantsLock = new();");
        openXrSource.ShouldContain("lock (_openXrPrimaryCommandBufferVariantsLock)");
        openXrSource.ShouldContain("MarkOpenXrPrimaryCommandBufferVariantsDirty()");
        openXrSource.ShouldContain("GetOrCreateOpenXrPrimaryCommandBufferVariant(");
        openXrSource.ShouldContain("TryReuseOpenXrPrimaryCommandBuffer(");
        openXrSource.ShouldContain("TryReuseOpenXrMirrorPrimaryCommandBuffer(");
        openXrSource.ShouldContain("DestroyOpenXrPrimaryCommandBufferCache()");
        openXrSource.ShouldContain("RecordOpenXrPrimaryCommandBuffer(");
        openXrSource.ShouldNotContain("lock (_openXrPrimaryCommandBufferVariantsLock)\r\n        {\r\n            ulong cacheKey = BuildOpenXrPrimaryCommandBufferCacheKey");
    }

    [Test]
    public void PrimaryCommandBufferRecording_UsesThreadLocalScratchForParallelEyeSafety()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferState.cs");
        string recordingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string secondarySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.SecondaryCommandBuffers.cs");

        stateSource.ShouldContain("ThreadLocal<CommandBufferRecordingScratch> _commandBufferRecordingScratch");
        stateSource.ShouldContain("private sealed class CommandBufferRecordingScratch");
        recordingSource.ShouldContain("CommandBufferRecordingScratch recordingScratch = _commandBufferRecordingScratch.Value!;");
        recordingSource.ShouldContain("recordingScratch.ExecutedCommandChainSecondaryHandles");
        recordingSource.ShouldContain("recordingScratch.SwapchainWritesByPipeline");
        recordingSource.ShouldContain("recordingScratch.FboLayoutTracking");
        secondarySource.ShouldContain("HashSet<nint> executedCommandChainSecondaryHandles");
        secondarySource.ShouldNotContain("_executedCommandChainSecondaryHandlesScratch");
        stateSource.ShouldNotContain("_swapchainWritesByPipelineScratch");
        stateSource.ShouldNotContain("_recordMeshDrawSlotsByRendererScratch");
        stateSource.ShouldNotContain("_fboLayoutTrackingScratch");
    }

    [Test]
    public void OpenXrSubmitDiagnostics_ReportFrameSlotsUploadsAndRetirementFlushes()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");

        openXrSource.ShouldContain("uint FrameDataSlotIndex");
        openXrSource.ShouldContain("CountOpenXrEyeRecordedTextureUploads()");
        openXrSource.ShouldContain("queueSubmitMs={1:F3} fenceWaitMs={2:F3}");
        openXrSource.ShouldContain("eye batch submit completed leftFrameSlot={0} rightFrameSlot={1} publishedUploads={2} retiredFlushSlots={3}");
        openXrSource.ShouldContain("eye batch submit did not complete leftFrameSlot={0} rightFrameSlot={1} cancelledUploads={2}");
        openXrSource.ShouldContain("eye batch submit failed leftFrameSlot={0} rightFrameSlot={1} cancelledUploads={2}");
        openXrSource.ShouldContain("MAX_FRAMES_IN_FLIGHT");
    }

    [Test]
    public void OpenXrEyeUploadPublicationBuffers_AreEyeScopedBeforeMergedSubmit()
    {
        VulkanRenderer.ResolveOpenXrEyeUploadPublicationBufferIndex(0u).ShouldBe(0);
        VulkanRenderer.ResolveOpenXrEyeUploadPublicationBufferIndex(1u).ShouldBe(1);
        VulkanRenderer.ResolveOpenXrEyeUploadPublicationBufferIndex(99u).ShouldBe(1);

        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        openXrSource.ShouldContain("private readonly List<VulkanImportedTexturePendingUpload>[] _openXrEyeRecordedTextureUploadsForSubmit = [new(), new()];");
        openXrSource.ShouldContain("MoveRecordedTextureUploadsForSubmitTo(eyeUploads);");
        openXrSource.ShouldContain("PublishOpenXrEyeRecordedTextureUploadsAfterCompletedSubmit(\"OpenXR eye batch\")");
        openXrSource.ShouldContain("CancelOpenXrEyeRecordedTextureUploads(\"OpenXR eye batch command buffer submit failed\")");
    }

    [Test]
    public void OpenXrEyeUploadPublicationBuffers_HandleRecordSubmitAndDeviceLostFailures()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");

        openXrSource.ShouldContain("ClearOpenXrEyeRecordedTextureUploads();");
        openXrSource.ShouldContain("hasFirst = TryRecordOpenXrEyeSwapchainCommandBuffer(firstEye, out firstRecorded);");
        openXrSource.ShouldContain("if (!hasFirst)");
        openXrSource.ShouldContain("hasSecond = TryRecordOpenXrEyeSwapchainCommandBuffer(secondEye, out secondRecorded);");
        openXrSource.ShouldContain("if (!hasSecond)");
        openXrSource.ShouldContain("PublishOpenXrEyeRecordedTextureUploadsAfterCompletedSubmit(\"OpenXR eye batch\")");
        openXrSource.ShouldContain("CancelOpenXrEyeRecordedTextureUploads(\"OpenXR eye batch command buffers did not complete\")");
        openXrSource.ShouldContain("if (!submitted && !commandBuffersCompleted && !IsDeviceLost)");
        openXrSource.ShouldContain("FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);");
        openXrSource.ShouldContain("FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);");
    }

    [Test]
    public void AllocatorBackedTextures_CacheViewsPerPhysicalImageContext()
    {
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");

        textureSource.ShouldContain("private readonly List<PhysicalImageViewCacheEntry> _physicalImageViewCache = [];");
        textureSource.ShouldContain("SaveCurrentPhysicalImageViewCache();");
        textureSource.ShouldContain("if (!TryRestorePhysicalImageViewCache(_physicalGroup, current))");
        textureSource.ShouldContain("private sealed record class PhysicalImageViewCacheEntry");
        textureSource.ShouldContain("DestroyCurrentViews(removeActiveCacheEntry: true);");
    }

    [Test]
    public void FrameBuffers_CacheAttachmentVariantsForSerialViewContextSwitches()
    {
        string frameBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");

        frameBufferSource.ShouldContain("private readonly List<CachedFrameBufferState> _cachedFrameBufferStates = [];");
        frameBufferSource.ShouldContain("if (TryActivateCachedFrameBufferState(attachments, fbWidth, fbHeight))");
        frameBufferSource.ShouldContain("CachedFrameBufferState state = CreateFrameBufferState(attachments, fbWidth, fbHeight);");
        frameBufferSource.ShouldContain("private sealed class CachedFrameBufferState");
        frameBufferSource.ShouldNotContain("Rebuilding framebuffer");
    }

    [Test]
    public void DescriptorImageInfoAndAllocationKeys_AreScopedToActivePhysicalPlannerContext()
    {
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string descriptorKeySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorAllocationKey.cs");
        string descriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");

        textureSource.ShouldContain("public DescriptorImageInfo CreateImageInfo()");
        textureSource.ShouldContain("RefreshPhysicalGroupImageIfStale();");
        textureSource.ShouldContain("ImageView = _view");
        textureSource.ShouldContain("Sampler = _sampler");
        textureSource.ShouldContain("if (!TryRestorePhysicalImageViewCache(_physicalGroup, current))");
        descriptorKeySource.ShouldContain("ulong ResourceFingerprint");
        descriptorKeySource.ShouldContain("int DescriptorFrameSlotCount");
        descriptorSource.ShouldContain("hash.Add(Renderer.ResourceAllocatorIdentity);");
        descriptorSource.ShouldContain("AppendComponent(builder, \"resourceAllocator\", unchecked((ulong)Renderer.ResourceAllocatorIdentity));");
        descriptorSource.ShouldContain("DescriptorAllocationKey allocationKey = new(schemaFingerprint, resourceFingerprint, descriptorFrameSlotCount, setCount);");
    }

    [Test]
    public void MeshImageDescriptors_UseCoherentSourceSnapshotsForParallelViewRecording()
    {
        string descriptorInterfaceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/IVkImageDescriptorSource.cs");
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string textureViewSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");
        string descriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");

        descriptorInterfaceSource.ShouldContain("internal readonly record struct VkImageDescriptorSnapshot");
        descriptorInterfaceSource.ShouldContain("bool TryGetDescriptorSnapshot(");
        descriptorInterfaceSource.ShouldContain("ImageAspectFlags? requestedAspectMask");
        textureSource.ShouldContain("private readonly object _imageStateLock = new();");
        textureSource.ShouldContain("TryBuildDescriptorSnapshotNoLock(requestedViewType, requestedAspectMask, out snapshot)");
        textureViewSource.ShouldContain("bool IVkImageDescriptorSource.TryGetDescriptorSnapshot(");

        int start = descriptorSource.IndexOf("private bool TryResolveImage(", StringComparison.Ordinal);
        int end = descriptorSource.IndexOf("private bool TryUsePlaceholderDescriptor(", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        end.ShouldBeGreaterThan(start);
        string tryResolveImageBody = descriptorSource[start..end];

        int samplerStart = descriptorSource.IndexOf("private bool TryResolveDescriptorSampler(", StringComparison.Ordinal);
        int samplerEnd = descriptorSource.IndexOf("private void LogPostProcessDescriptor(", StringComparison.Ordinal);
        samplerStart.ShouldBeGreaterThanOrEqualTo(0);
        samplerEnd.ShouldBeGreaterThan(samplerStart);
        string tryResolveDescriptorSamplerBody = descriptorSource[samplerStart..samplerEnd];

        tryResolveImageBody.ShouldContain("source.TryGetDescriptorSnapshot(");
        tryResolveImageBody.ShouldContain("descriptorSnapshot.Usage");
        tryResolveImageBody.ShouldContain("descriptorSnapshot.Format");
        tryResolveImageBody.ShouldContain("descriptorSnapshot.Aspect");
        tryResolveImageBody.ShouldContain("descriptorSnapshot.View");
        tryResolveImageBody.ShouldContain("TryResolveDescriptorSampler(binding, descriptorType, in descriptorSnapshot");
        tryResolveImageBody.ShouldContain("Renderer.ResolveDescriptorImageLayout(source, in descriptorSnapshot, descriptorType)");
        tryResolveImageBody.ShouldNotContain("source.TryEnsureDescriptorReadyForUse");
        tryResolveImageBody.ShouldNotContain("source.DescriptorUsage");
        tryResolveImageBody.ShouldNotContain("source.DescriptorFormat");
        tryResolveImageBody.ShouldNotContain("source.DescriptorAspect");
        tryResolveImageBody.ShouldNotContain("source.DescriptorView");
        tryResolveImageBody.ShouldNotContain("source.DescriptorSampler");
        tryResolveImageBody.ShouldNotContain("source.GetDepthOnlyDescriptorView()");
        tryResolveImageBody.ShouldNotContain("source.GetStencilOnlyDescriptorView()");
        tryResolveDescriptorSamplerBody.ShouldContain("in VkImageDescriptorSnapshot snapshot");
        tryResolveDescriptorSamplerBody.ShouldContain("snapshot.Sampler");
    }

    [Test]
    public void ImageBackedTextureAttachmentViewCache_IsImageStateLockedForParallelEyeRecording()
    {
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");

        textureSource.ShouldContain("private readonly object _imageStateLock = new();");
        textureSource.ShouldContain("private void RefreshPhysicalGroupImageIfStaleNoLock()");
        textureSource.ShouldContain("lock (_imageStateLock)\n                RefreshPhysicalGroupImageIfStaleNoLock();");

        int attachmentViewStart = textureSource.IndexOf("public ImageView GetAttachmentView(", StringComparison.Ordinal);
        int attachmentExtentStart = textureSource.IndexOf("bool IVkFrameBufferAttachmentSource.TryGetAttachmentExtent(", StringComparison.Ordinal);
        int descriptorInfoStart = textureSource.IndexOf("public DescriptorImageInfo CreateImageInfo()", StringComparison.Ordinal);
        int descriptorInfoEnd = textureSource.IndexOf("#endregion", descriptorInfoStart, StringComparison.Ordinal);
        attachmentViewStart.ShouldBeGreaterThanOrEqualTo(0);
        attachmentExtentStart.ShouldBeGreaterThan(attachmentViewStart);
        descriptorInfoStart.ShouldBeGreaterThan(attachmentExtentStart);
        descriptorInfoEnd.ShouldBeGreaterThan(descriptorInfoStart);

        string attachmentViewBody = textureSource[attachmentViewStart..attachmentExtentStart];
        string descriptorInfoBody = textureSource[descriptorInfoStart..descriptorInfoEnd];

        attachmentViewBody.ShouldContain("lock (_imageStateLock)");
        attachmentViewBody.ShouldContain("RefreshPhysicalGroupImageIfStaleNoLock();");
        attachmentViewBody.ShouldContain("_attachmentViews.TryGetValue");
        attachmentViewBody.ShouldContain("_attachmentViews[key] = cached;");
        descriptorInfoBody.ShouldContain("lock (_imageStateLock)");
        descriptorInfoBody.ShouldContain("RefreshPhysicalGroupImageIfStaleNoLock();");
    }

    [Test]
    public void CommandAndFramebufferCacheKeys_IncludeViewPlannerAndFoveationIdentity()
    {
        CommandChainKey left = new(
            FrameSlot: 3,
            ViewKey: new RenderViewKey(10, 20, VulkanRenderer.CommandChainLeftEyeViewIndex, RenderViewKind.VREye, 0, -1),
            PassIndex: 4,
            TargetIdentity: 5,
            DynamicOverlay: false,
            ChainOrdinal: 0);
        CommandChainKey right = left with
        {
            ViewKey = left.ViewKey with { ViewIndex = VulkanRenderer.CommandChainRightEyeViewIndex },
            FrameSlot = 4,
        };

        left.ShouldNotBe(right);

        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string frameBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");

        openXrSource.ShouldContain("hash.Add(targetContext.Image.Handle);");
        openXrSource.ShouldContain("hash.Add(targetContext.ImageView.Handle);");
        openXrSource.ShouldContain("hash.Add(targetContext.DepthImage.Handle);");
        openXrSource.ShouldContain("hash.Add(targetContext.DepthView.Handle);");
        openXrSource.ShouldContain("hash.Add(targetContext.OpenXrViewIndex);");
        openXrSource.ShouldContain("hash.Add(targetContext.OpenXrImageIndex);");
        openXrSource.ShouldContain("hash.Add(targetContext.FrameDataSlotIndex);");
        openXrSource.ShouldContain("hash.Add(targetContext.ResourcePlannerStateIndex);");
        openXrSource.ShouldContain("hash.Add(targetContext.FoveationResourceKey);");
        openXrSource.ShouldContain("hash.Add((int)targetContext.FoveationAttachmentKind);");
        openXrSource.ShouldContain("hash.Add(targetContext.FoveationAttachmentOwnedByResourcePlanner);");

        frameBufferSource.ShouldContain("if (AttachmentViews[i].Handle != attachments[i].View.Handle)");
        frameBufferSource.ShouldContain("if (!AttachmentSignature[i].Equals(attachments[i].Signature))");
        frameBufferSource.ShouldContain("if (!AttachmentTargets[i].Equals(attachments[i].TargetInfo))");
    }

    [Test]
    public void OpenXrParallelEyePreparation_UsesDistinctImmutablePlannerContextsBeforeWorkerRecord()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string workerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.EyeRecordWorkers.cs");

        workerSource.ShouldContain("TryPrepareOpenXrEyeSwapchainCommandBuffer(firstEye, out OpenXrPreparedEyeCommandBufferInput preparedFirstEye)");
        workerSource.ShouldContain("TryPrepareOpenXrEyeSwapchainCommandBuffer(secondEye, out OpenXrPreparedEyeCommandBufferInput preparedSecondEye)");
        workerSource.ShouldContain("DispatchOpenXrEyeRecordWorkers(preparedFirstEye, preparedSecondEye)");
        workerSource.ShouldContain("private OpenXrPreparedEyeCommandBufferInput _prepared;");
        workerSource.ShouldNotContain("Task.Run");

        openXrSource.ShouldContain("private readonly record struct OpenXrPreparedEyeCommandBufferInput");
        openXrSource.ShouldContain("OpenXrEyeRenderTargetContext TargetContext");
        openXrSource.ShouldContain("FrameOp[] Ops");
        openXrSource.ShouldContain("FrameOpContext PlannerContext");
        openXrSource.ShouldContain("plannerContext,");
        openXrSource.ShouldContain("CommandChainSchedule? CommandChainSchedule");
        openXrSource.ShouldContain("EnterOpenXrResourcePlannerThreadScope(OpenXrViewResourcePlannerContextKey.FromTarget(in targetContext))");
        openXrSource.ShouldContain("ResetDynamicUniformRingBuffer(recordImageIndex);");
        openXrSource.ShouldNotContain("renderer.RestoreResourcePlannerRuntimeState(openXrState)");
    }

    [Test]
    public void OpenXrVulkanViewRenderModes_DispatchToDistinctRendererPaths()
    {
        string openXrApiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs");
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string workerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.EyeRecordWorkers.cs");

        openXrApiSource.ShouldContain("TryRenderVulkanEyeSinglePassStereoToSwapchains");
        openXrApiSource.ShouldContain("TryRenderVulkanEyeParallelCommandBufferRecordingToSwapchains");
        openXrApiSource.ShouldContain("EVrViewRenderMode.SinglePassStereo => renderer.TryRenderOpenXrEyeSwapchainsSinglePassStereo");
        openXrApiSource.ShouldContain("EVrViewRenderMode.ParallelCommandBufferRecording => renderer.TryRenderOpenXrEyeSwapchainsParallelCommandBufferRecording");
        rendererSource.ShouldContain("internal bool TryRenderOpenXrEyeSwapchainsSinglePassStereo");
        rendererSource.ShouldContain("internal bool TryRenderOpenXrEyeSwapchainsParallelCommandBufferRecording");
        rendererSource.ShouldContain("TryRenderOpenXrEyeSwapchainsWithParallelEyeWorkers(leftEye, rightEye)");
        workerSource.ShouldContain("private sealed class OpenXrEyeRecordWorkerScheduler");
        workerSource.ShouldContain("private sealed class OpenXrEyeRecordWorker");
    }

    [Test]
    public void OpenXrExternalSwapchainTargets_DisableHistoryBasedAaAndTsrScaling()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        string pipeline2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        string postProcessSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
        string postProcess2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.PostProcessing.cs").Replace("\r\n", "\n");
        string temporalSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs").Replace("\r\n", "\n");

        foreach (string source in new[] { pipelineSource, pipeline2Source })
        {
            source.ShouldContain("if (mode == EAntiAliasingMode.Tsr && DisableHistoryBasedVrEffects())\n            return null;");
            source.ShouldContain("&& !DisableHistoryBasedVrEffects()\n        && ResolveAntiAliasingMode() == EAntiAliasingMode.Tsr;");
            source.ShouldContain("private static bool RuntimeNeedsTemporalAaVelocityBuffer");
            source.ShouldContain("=> !DisableHistoryBasedVrEffects()\n        && ResolveAntiAliasingMode() is EAntiAliasingMode.Taa or EAntiAliasingMode.Dlaa;");
            source.ShouldContain("|| RuntimeNeedsTemporalAaVelocityBuffer");
        }

        foreach (string source in new[] { postProcessSource, postProcess2Source })
        {
            source.ShouldContain("private static bool DisableHistoryBasedVrEffects()\n        => !VPRC_TemporalAccumulationPass.TryUseHistoryBasedVrEffects(out _, out _);");
        }

        temporalSource.ShouldContain("ShouldDisableHistoryBasedVrAntiAliasing()");
        temporalSource.ShouldContain("internal static EVrTemporalHistoryPolicy ResolveHistoryIsolationPolicy(out string reason)");
        temporalSource.ShouldContain("EVrTemporalHistoryPolicy.StereoArrayLayer => \"true single-pass stereo array-layer history\"");
        temporalSource.ShouldContain("EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain => \"external per-eye swapchain targets\"");
        temporalSource.ShouldContain("VrViewRenderModeResolver.Resolve");
    }

    [Test]
    public void OpenXrStereoTemporalHistory_UsesPerViewStateAndArrayShaders()
    {
        string temporalSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs").Replace("\r\n", "\n");
        string texturesSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs").Replace("\r\n", "\n");
        string textures2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.Textures.cs").Replace("\r\n", "\n");
        string fboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs").Replace("\r\n", "\n");
        string fbo2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.FBOs.cs").Replace("\r\n", "\n");
        string temporalStereoShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/TemporalAccumulationStereo.fs").Replace("\r\n", "\n");
        string tsrStereoShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolutionStereo.fs").Replace("\r\n", "\n");
        string motionVectorStereoShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/MotionVectorsStereo.fs").Replace("\r\n", "\n");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs").Replace("\r\n", "\n");
        string meshUniformsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs").Replace("\r\n", "\n");

        temporalSource.ShouldContain("internal readonly record struct TemporalViewKey");
        temporalSource.ShouldContain("private static readonly Dictionary<TemporalViewKey, TemporalState> TemporalStates");
        temporalSource.ShouldContain("public TemporalEyeState LeftEye { get; } = new();");
        temporalSource.ShouldContain("public TemporalEyeState RightEye { get; } = new();");
        temporalSource.ShouldContain("RightEyePrevViewProjectionUnjittered");
        temporalSource.ShouldNotContain("ConditionalWeakTable<XRCamera, TemporalState>");

        foreach (string source in new[] { texturesSource, textures2Source })
        {
            source.ShouldContain("XRTexture2DArray stereoTexture = XRTexture2DArray.CreateFrameBufferTexture");
            source.ShouldContain("stereoTexture.SamplerName = TsrOutputTextureName;");
            source.ShouldContain("stereoTexture.SamplerName = TsrHistoryColorTextureName;");
            source.ShouldContain("stereoTexture.OVRMultiViewParameters = new(0, 2u);");
        }

        foreach (string source in new[] { fboSource, fbo2Source })
        {
            source.ShouldContain("Stereo ? \"TemporalSuperResolutionStereo.fs\" : \"TemporalSuperResolution.fs\"");
            source.ShouldContain("Stereo ? \"TemporalAccumulationStereo.fs\" : \"TemporalAccumulation.fs\"");
        }

        temporalStereoShader.ShouldContain("uniform sampler2DArray TemporalColorInput;");
        temporalStereoShader.ShouldContain("uniform sampler2DArray HistoryColor;");
        temporalStereoShader.ShouldContain("gl_ViewID_OVR");
        tsrStereoShader.ShouldContain("uniform sampler2DArray TsrHistoryColor;");
        tsrStereoShader.ShouldContain("uniform usampler2DArray StencilView;");
        tsrStereoShader.ShouldContain("gl_ViewID_OVR");
        motionVectorStereoShader.ShouldContain("uniform mat4 CurrViewProjectionStereo[2];");
        motionVectorStereoShader.ShouldContain("uniform mat4 PrevViewProjectionStereo[2];");
        motionVectorStereoShader.ShouldContain("int eyeIndex = int(gl_ViewID_OVR);");

        meshSource.ShouldContain("Matrix4x4 PreviousRightEyeViewMatrix");
        meshSource.ShouldContain("previousRightEyeProjectionMatrixSnapshot = temporalData.RightEyePrevProjection;");
        meshUniformsSource.ShouldContain("case nameof(EEngineUniform.PrevRightEyeViewMatrix):\n\t\t\t\t\tvalue = draw.PreviousRightEyeViewMatrix;");
        meshUniformsSource.ShouldContain("case nameof(EEngineUniform.PrevRightEyeProjMatrix):\n\t\t\t\t\treturn UploadUniform(buffer, draw.PreviousRightEyeProjectionMatrix);");

        fboSource.ShouldContain("Stereo ? \"MotionVectorsStereo.fs\" : \"MotionVectors.fs\"");
        fboSource.ShouldContain("program.Uniform(\"CurrViewProjectionStereo\", _motionVectorCurrViewProjectionStereo);");
        fbo2Source.ShouldContain("Stereo ? \"MotionVectorsStereo.fs\" : \"MotionVectors.fs\"");
        fbo2Source.ShouldContain("program.Uniform(\"PrevViewProjectionStereo\", _motionVectorPrevViewProjectionStereo);");
    }

    [Test]
    public void VulkanStereoVariantSelection_DoesNotUseNvStereoSemantics()
    {
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs");
        string uiBatchSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/UI/UIBatchCollector.cs");
        string defaultGeneratorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Shaders/Generator/DefaultVertexShaderGenerator.cs");
        string deformGeneratorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Shaders/Generator/MeshDeformVertexShaderGenerator.cs");
        string shaderCompilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderCompiler.cs");

        meshSource.ShouldContain("bool allowNvStereo = !RuntimeEngine.Rendering.State.IsVulkan;");
        meshSource.ShouldContain("bool preferNV = allowNvStereo && RuntimeEngine.Rendering.Settings.PreferNVStereo;");
        meshSource.ShouldContain("stereoPass && allowNvStereo && hasNvMaterialVertexShader");
        uiBatchSource.ShouldContain("if (!RuntimeEngine.Rendering.State.IsVulkan");

        defaultGeneratorSource.ShouldContain("RuntimeEngine.Rendering.State.IsVulkan");
        defaultGeneratorSource.ShouldContain("Line(\"#extension GL_EXT_multiview : require\");");
        defaultGeneratorSource.ShouldContain("Line(\"#extension GL_NV_stereo_view_rendering : require\");");
        defaultGeneratorSource.ShouldContain("RuntimeEngine.Rendering.State.IsVulkan ? \"gl_ViewIndex\" : \"gl_ViewID_OVR\"");
        deformGeneratorSource.ShouldContain("RuntimeEngine.Rendering.State.IsVulkan");
        deformGeneratorSource.ShouldContain("Line(\"#extension GL_EXT_multiview : require\");");
        deformGeneratorSource.ShouldContain("Line(\"#extension GL_NV_stereo_view_rendering : require\");");
        deformGeneratorSource.ShouldContain("RuntimeEngine.Rendering.State.IsVulkan ? \"gl_ViewIndex\" : \"gl_ViewID_OVR\"");
        shaderCompilerSource.ShouldContain("LogVulkanStereoRewrite(shaderName, \"OVR_multiview/gl_ViewID_OVR\", \"GL_EXT_multiview/gl_ViewIndex\")");
        shaderCompilerSource.ShouldContain("LogVulkanStereoRewrite(shaderName, \"NV_stereo_view_rendering\", \"GL_EXT_multiview-compatible shader\")");
        shaderCompilerSource.ShouldContain("[VulkanShaderCompiler] Rewrote stereo shader");
    }

    [Test]
    public void VulkanDynamicRenderingMultiviewContracts_PropagateViewMaskAcrossBeginInheritanceAndPipeline()
    {
        string renderTargetModeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderTargetMode.cs");
        string framebufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string secondarySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.SecondaryCommandBuffers.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string openXrApiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs");

        renderTargetModeSource.ShouldContain("public uint ViewMask { get; }");
        renderTargetModeSource.ShouldContain("public uint LayerCount { get; }");
        renderTargetModeSource.ShouldContain("private static uint ResolveDynamicRenderingLayerCount(uint framebufferLayers, uint viewMask)");
        renderTargetModeSource.ShouldContain("viewMask=0x{signature.ViewMask:X}");
        renderTargetModeSource.ShouldContain("layers={signature.LayerCount}");

        framebufferSource.ShouldContain("public uint MultiviewViewMask { get; private set; }");
        framebufferSource.ShouldContain("ResolveFramebufferMultiviewViewMask(attachments)");
        framebufferSource.ShouldContain("OVRMultiViewParameters is { NumViews: > 1u }");
        framebufferSource.ShouldContain("BuildMultiviewViewMask(ovr, layerCount)");
        framebufferSource.ShouldContain("MultiviewViewMask = state.MultiviewViewMask;");

        commandBufferSource.ShouldContain("DynamicRenderingFormatSignature targetDynamicRenderingFormats = CreateDynamicRenderingFormatSignature(");
        commandBufferSource.ShouldContain("fboLayerCount);");
        commandBufferSource.ShouldContain("ViewMask = targetDynamicRenderingFormats.ViewMask");
        commandBufferSource.ShouldContain("LayerCount = targetDynamicRenderingFormats.LayerCount");
        commandBufferSource.ShouldContain("ViewMask = inheritedDynamicRenderingFormats.ViewMask");
        commandBufferSource.ShouldContain("ResolveDynamicRenderingLayerCount(vkFrameBuffer.FramebufferLayers, fboViewMask)");
        commandBufferSource.ShouldContain("viewMask=0x{9:X}");

        secondarySource.ShouldContain("ViewMask = dynamicRenderingFormats.ViewMask");
        pipelineSource.ShouldContain("ViewMask = request.DynamicRenderingFormats.ViewMask");
        openXrApiSource.ShouldContain("Vulkan dynamic rendering is required for OpenXR true single-pass stereo multiview");
        openXrApiSource.ShouldContain("dynamicRendering={(Window?.Renderer is VulkanRenderer renderer && renderer.UseDynamicRenderingRenderTargets)}");
    }

    [Test]
    public void OpenXrParallelEyeRecording_UsesBoundedWorkersAndDeterministicFailureHandling()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string openXrApiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs");
        string workerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.EyeRecordWorkers.cs");

        workerSource.ShouldContain("private OpenXrEyeRecordWorkerScheduler? _openXrEyeRecordWorkerScheduler;");
        workerSource.ShouldContain("OpenXrEyeRecordWorkerScheduler scheduler = EnsureOpenXrEyeRecordWorkerScheduler();");
        workerSource.ShouldContain("_left.Start(renderer, leftEye);");
        workerSource.ShouldContain("_right.Start(renderer, rightEye);");
        workerSource.ShouldContain("OpenXrEyeRecordWorkerResult left = _left.Wait();");
        workerSource.ShouldContain("OpenXrEyeRecordWorkerResult right = _right.Wait();");
        workerSource.ShouldContain("TryPrepareOpenXrEyeSwapchainCommandBuffer(firstEye, out OpenXrPreparedEyeCommandBufferInput preparedFirstEye)");
        workerSource.ShouldContain("TryPrepareOpenXrEyeSwapchainCommandBuffer(secondEye, out OpenXrPreparedEyeCommandBufferInput preparedSecondEye)");
        workerSource.ShouldContain("DispatchOpenXrEyeRecordWorkers(preparedFirstEye, preparedSecondEye)");
        workerSource.ShouldContain("private OpenXrPreparedEyeCommandBufferInput _prepared;");
        openXrSource.ShouldContain("RefreshFrameOpResourceWrappers(");
        openXrSource.ShouldContain("PrewarmOpenXrFrameOpResources(ops);");
        openXrSource.ShouldContain("TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in prepared, out recorded)");
        openXrSource.ShouldContain("using ThreadRenderStateScope renderStateScope = EnterThreadRenderStateScope(");
        openXrSource.ShouldContain("CreateOpenXrEyeRenderStateTracker(in targetContext)");
        openXrSource.ShouldContain("EnterOpenXrResourcePlannerThreadScope(OpenXrViewResourcePlannerContextKey.FromTarget(in targetContext))");
        workerSource.ShouldContain("TryRecordOpenXrEyeSwapchainCommandBufferFromWorker");
        workerSource.ShouldContain("thread-scoped prepared primary record");
        workerSource.ShouldContain("private readonly object _openXrParallelEyePrimaryRecordSharedStateLock = new();");
        workerSource.ShouldContain("lock (_openXrParallelEyePrimaryRecordSharedStateLock)");
        workerSource.ShouldContain("shared objects");
        workerSource.ShouldContain("command-buffer-local layout state");
        workerSource.ShouldContain("leftSuccess={0} rightSuccess={1}");
        workerSource.ShouldContain("if (!hasFirst || !hasSecond)");
        workerSource.ShouldContain("LogOpenXrEyeRecordWorkerFailure(workerBatch);");
        workerSource.ShouldContain("SubmitAndWaitOpenXrCommandBuffers(");
        workerSource.ShouldContain("DestroyOpenXrEyeRecordWorkers()");
        openXrSource.ShouldContain("DestroyOpenXrEyeRecordWorkers();");
        openXrApiSource.ShouldContain("serialized shared Vulkan layout-state recording");
        workerSource.ShouldNotContain("Task.Run");
    }

    [Test]
    public void BuildRenderViewKey_ShadowPassIncludesLightAndCascadeIdentity()
    {
        RenderPassMetadata shadowPass = new(5, "DirectionalShadowCascade", ERenderGraphPassStage.Graphics);
        LayeredShadowUniformState shadowState = new()
        {
            IsShadowPass = true,
            DirectionalCascadeInstancedLayeredShadowPass = true,
            DirectionalCascadeShadowLayerCount = 4,
        };
        VulkanRenderer.MeshDrawOp op = CreateMeshDrawOp(
            default(VulkanRenderer.PendingMeshDraw) with { ShadowUniformState = shadowState },
            passIndex: 5,
            context: CreateFrameOpContext(passMetadata: [shadowPass]));

        RenderViewKey key = VulkanRenderer.BuildRenderViewKey(op, dynamicOverlay: false);

        key.Kind.ShouldBe(RenderViewKind.Shadow);
        key.LightIdentity.ShouldNotBe(0);
        key.CascadeIndex.ShouldBe(3);
        key.ViewIndex.ShouldBe(3);
    }

    [Test]
    public void ShadowCommandChainStructuralSignature_ChangesForAtlasPackingState()
    {
        LayeredShadowUniformState fourCascadeState = new()
        {
            IsShadowPass = true,
            DirectionalCascadeInstancedLayeredShadowPass = true,
            DirectionalCascadeShadowLayerCount = 4,
        };
        LayeredShadowUniformState twoCascadeState = fourCascadeState;
        twoCascadeState.DirectionalCascadeShadowLayerCount = 2;

        VulkanRenderer.ComputeShadowCommandChainStructuralSignature(fourCascadeState)
            .ShouldNotBe(VulkanRenderer.ComputeShadowCommandChainStructuralSignature(twoCascadeState));
    }

    [Test]
    public void ValidateCommandChainShadowFallbackMode_AllowsOnlyExplicitReusableShadowFallbacks()
    {
        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainShadowFallbackMode(ShadowFallbackMode.None, shadowTileResident: true));
        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainShadowFallbackMode(ShadowFallbackMode.StaleTile, shadowTileResident: true));
        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainShadowFallbackMode(ShadowFallbackMode.Lit, shadowTileResident: false));
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainShadowFallbackMode(ShadowFallbackMode.Legacy, shadowTileResident: true))
            .Message.ShouldContain("fallback mode");
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainShadowFallbackMode(ShadowFallbackMode.None, shadowTileResident: false))
            .Message.ShouldContain("explicit fallback");
    }

    [Test]
    public void CommandChainKey_IncludesFrameSlotAndOrdinal()
    {
        RenderViewKey view = new(1, 2, 0, RenderViewKind.Main, 0, -1);
        CommandChainKey slotZero = new(0, view, 3, 4, false, 5);
        CommandChainKey slotOne = slotZero with { FrameSlot = 1 };
        CommandChainKey differentOrdinal = slotZero with { ChainOrdinal = 6 };
        CommandChainKey dynamicOverlay = slotZero with { DynamicOverlay = true };

        slotZero.ShouldNotBe(slotOne);
        slotZero.ShouldNotBe(differentOrdinal);
        slotZero.ShouldNotBe(dynamicOverlay);
        slotZero.ShouldBe(new CommandChainKey(0, view, 3, 4, false, 5));
    }

    [Test]
    public void RenderPacketVolatility_Order_IsIntentionalForDiagnostics()
    {
        ((int)RenderPacketVolatility.StaticStructural).ShouldBe(0);
        ((int)RenderPacketVolatility.FrameDataOnly).ShouldBe(1);
        ((int)RenderPacketVolatility.DynamicCommand).ShouldBe(2);
        ((int)RenderPacketVolatility.StructuralDirty).ShouldBe(3);
    }

    [Test]
    public void ClassifyRenderPacketVolatility_StaticClearAndBarrier_AreStaticStructural()
    {
        VulkanRenderer.FrameOpContext context = CreateFrameOpContext();
        VulkanRenderer.ClearOp clear = new(
            PassIndex: 0,
            Target: null,
            ClearColor: true,
            ClearDepth: true,
            ClearStencil: false,
            Color: default,
            Depth: 1.0f,
            Stencil: 0,
            Rect: default,
            Context: context);
        VulkanRenderer.MemoryBarrierOp barrier = new(
            PassIndex: 0,
            Mask: EMemoryBarrierMask.TextureFetch,
            Context: context);

        VulkanRenderer.ClassifyRenderPacketVolatility(clear, dynamicOverlay: false)
            .ShouldBe(RenderPacketVolatility.StaticStructural);
        VulkanRenderer.ClassifyRenderPacketVolatility(barrier, dynamicOverlay: false)
            .ShouldBe(RenderPacketVolatility.StaticStructural);
    }

    [Test]
    public void ClassifyRenderPacketVolatility_OverlayPassMetadata_IsDynamicCommand()
    {
        RenderPassMetadata overlayPass = new(7, "ProfilerOverlay", ERenderGraphPassStage.Graphics);
        VulkanRenderer.FrameOpContext context = CreateFrameOpContext(passMetadata: [overlayPass]);
        VulkanRenderer.ClearOp clear = new(
            PassIndex: 7,
            Target: null,
            ClearColor: true,
            ClearDepth: false,
            ClearStencil: false,
            Color: default,
            Depth: 1.0f,
            Stencil: 0,
            Rect: default,
            Context: context);

        VulkanRenderer.ClassifyRenderPacketVolatility(clear, dynamicOverlay: false)
            .ShouldBe(RenderPacketVolatility.DynamicCommand);
    }

    [Test]
    public void ClassifyRenderPacketVolatility_DynamicOverlayFlag_OverridesStaticOp()
    {
        VulkanRenderer.ClearOp clear = new(
            PassIndex: 0,
            Target: null,
            ClearColor: true,
            ClearDepth: true,
            ClearStencil: false,
            Color: default,
            Depth: 1.0f,
            Stencil: 0,
            Rect: default,
            Context: CreateFrameOpContext());

        VulkanRenderer.ClassifyRenderPacketVolatility(clear, dynamicOverlay: true)
            .ShouldBe(RenderPacketVolatility.DynamicCommand);
    }

    [Test]
    public void ClassifyRenderPacketVolatility_ComputeDispatch_IsFrameDataOnlyUnlessOverlay()
    {
        VulkanRenderer.ComputeDispatchOp compute = CreateComputeDispatchOp();

        VulkanRenderer.ClassifyRenderPacketVolatility(compute, dynamicOverlay: false)
            .ShouldBe(RenderPacketVolatility.FrameDataOnly);
        VulkanRenderer.ClassifyRenderPacketVolatility(compute, dynamicOverlay: true)
            .ShouldBe(RenderPacketVolatility.DynamicCommand);
    }

    [Test]
    public void CommandChainDirtyReason_FrameDataOnlyChange_RemainsReusable()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature + 1,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.None);
    }

    [Test]
    public void TryRefreshReusableCommandChainFrameData_UpdatesFrameDataSignature()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket packet = CreatePacket(
            structuralSignature: baseline.StructuralSignature,
            frameDataSignature: baseline.FrameDataSignature + 1,
            resourcePlanRevision: baseline.ResourcePlanSnapshot.Revision,
            descriptorGeneration: baseline.DescriptorSnapshot.DescriptorGeneration,
            pipelineGeneration: baseline.ResourcePlanSnapshot.PipelineGeneration,
            descriptorSetCount: baseline.DescriptorSnapshot.DescriptorSetCount,
            descriptorSetSignature: baseline.DescriptorSnapshot.DescriptorSetSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, packet)
            .ShouldBeTrue();
        chain.FrameDataSignature.ShouldBe(packet.FrameDataSignature);
    }

    [Test]
    public void TryRefreshReusableCommandChainFrameData_RejectsDescriptorContentChange()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x600,
            volatility: RenderPacketVolatility.FrameDataOnly);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket packet = CreatePacket(
            structuralSignature: baseline.StructuralSignature,
            frameDataSignature: baseline.FrameDataSignature + 1,
            resourcePlanRevision: baseline.ResourcePlanSnapshot.Revision,
            descriptorGeneration: baseline.DescriptorSnapshot.DescriptorGeneration + 1,
            pipelineGeneration: baseline.ResourcePlanSnapshot.PipelineGeneration,
            descriptorSetCount: baseline.DescriptorSnapshot.DescriptorSetCount,
            descriptorSetSignature: baseline.DescriptorSnapshot.DescriptorSetSignature + 1,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(
                CommandChainDirtyReason.Structure |
                CommandChainDirtyReason.DescriptorGeneration);
        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, packet)
            .ShouldBeFalse();
        chain.FrameDataSignature.ShouldBe(baseline.FrameDataSignature);
    }

    [Test]
    public void TryRefreshReusableCommandChainFrameData_AllowsUniformOnlyFrameDataChange()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x600,
            volatility: RenderPacketVolatility.FrameDataOnly);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket packet = CreatePacket(
            structuralSignature: baseline.StructuralSignature,
            frameDataSignature: baseline.FrameDataSignature + 1,
            resourcePlanRevision: baseline.ResourcePlanSnapshot.Revision,
            descriptorGeneration: baseline.DescriptorSnapshot.DescriptorGeneration,
            pipelineGeneration: baseline.ResourcePlanSnapshot.PipelineGeneration,
            descriptorSetCount: baseline.DescriptorSnapshot.DescriptorSetCount,
            descriptorSetSignature: baseline.DescriptorSnapshot.DescriptorSetSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.None);
        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, packet)
            .ShouldBeTrue();
        chain.FrameDataSignature.ShouldBe(packet.FrameDataSignature);
        chain.FrameDataRefreshTouchedDescriptors.ShouldBeFalse();
    }

    [Test]
    public void TryRefreshReusableCommandChainFrameData_ComputeDispatchRequiresMatchingDescriptors()
    {
        DispatchPacket[] dispatches =
        [
            new DispatchPacket(0, ProgramIdentity: 1, GroupsX: 1, GroupsY: 1, GroupsZ: 1, StructuralSignature: 0x100, FrameDataSignature: 0x200),
        ];
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x401,
            dispatches: dispatches);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket descriptorChanged = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x201,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x401,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x402,
            dispatches: dispatches);

        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, descriptorChanged)
            .ShouldBeFalse();

        RenderPacket uniformOnlyChanged = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x202,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x401,
            dispatches: dispatches);

        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, uniformOnlyChanged)
            .ShouldBeTrue();
        chain.FrameDataRefreshTouchedDescriptors.ShouldBeFalse();
    }

    [Test]
    public void TryRefreshReusableCommandChainFrameData_RejectsDescriptorGenerationChange()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x600,
            volatility: RenderPacketVolatility.FrameDataOnly);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket packet = CreatePacket(
            structuralSignature: baseline.StructuralSignature,
            frameDataSignature: baseline.FrameDataSignature + 1,
            resourcePlanRevision: baseline.ResourcePlanSnapshot.Revision,
            descriptorGeneration: baseline.DescriptorSnapshot.DescriptorGeneration + 1,
            pipelineGeneration: baseline.ResourcePlanSnapshot.PipelineGeneration,
            descriptorSetCount: baseline.DescriptorSnapshot.DescriptorSetCount,
            descriptorSetSignature: baseline.DescriptorSnapshot.DescriptorSetSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.DescriptorGeneration);
        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, packet)
            .ShouldBeFalse();
        chain.FrameDataSignature.ShouldBe(baseline.FrameDataSignature);
        chain.DescriptorGeneration.ShouldBe(baseline.DescriptorSnapshot.DescriptorGeneration);
        chain.FrameDataRefreshTouchedDescriptors.ShouldBeFalse();
    }

    [Test]
    public void TryRefreshReusableCommandChainFrameData_RejectsStaticAndStructurallyDirtyPackets()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket staticPacket = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature + 1,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            volatility: RenderPacketVolatility.StaticStructural);
        RenderPacket structurallyDirtyPacket = CreatePacket(
            structuralSignature: chain.StructuralSignature + 1,
            frameDataSignature: chain.FrameDataSignature + 2,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, staticPacket)
            .ShouldBeFalse();
        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, structurallyDirtyPacket)
            .ShouldBeFalse();
        chain.FrameDataSignature.ShouldBe(0x200UL);
    }

    [Test]
    public void CommandChainDirtyReason_DetectsStructuralChange()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature + 1,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.Structure);
    }

    [Test]
    public void CommandChainDirtyReason_DetectsDescriptorResourceAndPipelineChanges()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision + 1,
            descriptorGeneration: chain.DescriptorGeneration + 1,
            pipelineGeneration: chain.PipelineGeneration + 1,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(
                CommandChainDirtyReason.ResourcePlan |
                CommandChainDirtyReason.DescriptorGeneration |
                CommandChainDirtyReason.PipelineGeneration);
    }

    [Test]
    public void CommandChainDirtyReason_DetectsPhysicalImageAndFramebufferChangesAsResourcePlan()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            physicalImageSignature: chain.PhysicalImageSignature + 1,
            framebufferSignature: chain.FramebufferSignature + 1,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.ResourcePlan);
    }

    [Test]
    public void ValidateReusableCommandChainReferences_AllowsCurrentSnapshots()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature + 1,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            physicalImageSignature: chain.PhysicalImageSignature,
            framebufferSignature: chain.FramebufferSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        Should.NotThrow(() => VulkanRenderer.ValidateReusableCommandChainReferences(chain, packet));
    }

    [Test]
    public void ValidateReusableCommandChainReferences_RejectsStaleDescriptorSets()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration + 1,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            physicalImageSignature: chain.PhysicalImageSignature,
            framebufferSignature: chain.FramebufferSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateReusableCommandChainReferences(chain, packet))
            .Message.ShouldContain("stale descriptor-set");
    }

    [Test]
    public void ValidateReusableCommandChainReferences_RejectsStalePhysicalImagesAndFramebuffers()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket stalePhysicalImage = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            physicalImageSignature: chain.PhysicalImageSignature + 1,
            framebufferSignature: chain.FramebufferSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);
        RenderPacket staleFramebuffer = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            physicalImageSignature: chain.PhysicalImageSignature,
            framebufferSignature: chain.FramebufferSignature + 1,
            volatility: RenderPacketVolatility.FrameDataOnly);

        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateReusableCommandChainReferences(chain, stalePhysicalImage))
            .Message.ShouldContain("stale physical-image");
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateReusableCommandChainReferences(chain, staleFramebuffer))
            .Message.ShouldContain("stale framebuffer");
    }

    [Test]
    public void ValidateReusableCommandChainReferences_RejectsStalePipelineHandles()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration + 1,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            physicalImageSignature: chain.PhysicalImageSignature,
            framebufferSignature: chain.FramebufferSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateReusableCommandChainReferences(chain, packet))
            .Message.ShouldContain("stale pipeline");
    }

    [Test]
    public void CommandChainDirtyReason_DetectsPacketShapeChangesAsStructure()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly,
            draws: [CreateDrawPacket(instanceCount: 1)]);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket packet = CreatePacket(
            structuralSignature: baseline.StructuralSignature,
            frameDataSignature: baseline.FrameDataSignature,
            resourcePlanRevision: baseline.ResourcePlanSnapshot.Revision,
            descriptorGeneration: baseline.DescriptorSnapshot.DescriptorGeneration,
            pipelineGeneration: baseline.ResourcePlanSnapshot.PipelineGeneration,
            descriptorSetCount: baseline.DescriptorSnapshot.DescriptorSetCount + 1,
            descriptorSetSignature: baseline.DescriptorSnapshot.DescriptorSetSignature + 1,
            volatility: RenderPacketVolatility.FrameDataOnly,
            draws: [CreateDrawPacket(instanceCount: 2), CreateDrawPacket(instanceCount: 3)]);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.Structure);
    }

    [Test]
    public void CommandChainDirtyReason_UnrecordedChain_DirtiesStructure()
    {
        CommandChain chain = new(new CommandChainKey(0, new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1), 3, 4, false, 5));
        RenderPacket packet = CreatePacket(
            structuralSignature: 10,
            frameDataSignature: 20,
            resourcePlanRevision: 30,
            descriptorGeneration: 40,
            pipelineGeneration: 50,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.Structure);
    }

    [Test]
    public void PrimaryCommandBufferDirtyReason_IsCleanForMatchingSchedule()
    {
        CommandChainSchedule schedule = CreateSchedule(dynamicOverlay: false, chainCount: 2);
        ulong groupSignature = VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(schedule);

        VulkanRenderer.EvaluatePrimaryCommandBufferDirtyReason(
                schedule,
                recordedScheduleSignature: schedule.StructuralSignature,
                recordedGroupSignature: groupSignature,
                recordedGroupCount: schedule.Groups.Length,
                recordedResourcePlanRevision: schedule.ResourcePlanRevision,
                recordedProfilerActive: false,
                recordedProfilerFrameSlot: -1,
                currentProfilerActive: false,
                currentProfilerFrameSlot: 0)
            .ShouldBe(PrimaryCommandBufferDirtyReason.None);
    }

    [Test]
    public void PrimaryCommandBufferDirtyReason_SeparatesScheduleResourceAndProfilerChanges()
    {
        CommandChainSchedule schedule = CreateSchedule(dynamicOverlay: false, chainCount: 2);

        VulkanRenderer.EvaluatePrimaryCommandBufferDirtyReason(
                schedule,
                recordedScheduleSignature: schedule.StructuralSignature + 1,
                recordedGroupSignature: VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(schedule) + 1,
                recordedGroupCount: schedule.Groups.Length + 1,
                recordedResourcePlanRevision: schedule.ResourcePlanRevision + 1,
                recordedProfilerActive: false,
                recordedProfilerFrameSlot: -1,
                currentProfilerActive: true,
                currentProfilerFrameSlot: 0)
            .ShouldBe(
                PrimaryCommandBufferDirtyReason.ScheduleStructure |
                PrimaryCommandBufferDirtyReason.GroupStructure |
                PrimaryCommandBufferDirtyReason.ResourcePlan |
                PrimaryCommandBufferDirtyReason.ProfilerMode);
    }

    [Test]
    public void PrimaryCommandBufferGroupSignature_ChangesWhenGroupShapeChanges()
    {
        CommandChainSchedule oneChain = CreateSchedule(dynamicOverlay: false, chainCount: 1);
        CommandChainSchedule twoChains = CreateSchedule(dynamicOverlay: false, chainCount: 2);
        CommandChainSchedule overlay = CreateSchedule(dynamicOverlay: true, chainCount: 1);

        VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(oneChain)
            .ShouldNotBe(VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(twoChains));
        VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(oneChain)
            .ShouldNotBe(VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(overlay));
    }

    [Test]
    public void ValidatePrimaryCommandChainSchedule_RequiresStaticGroupsBeforeOverlayGroups()
    {
        VulkanRenderer.ClearOp firstStatic = CreateClearOp(passIndex: 0);
        VulkanRenderer.ClearOp secondStatic = CreateClearOp(passIndex: 0);
        CommandChainSchedule valid = new(
            structuralSignature: 0x100,
            resourcePlanRevision: 0x200,
            groups: new[]
            {
                CreateGroup(passIndex: 0, targetIdentity: 0, dynamicOverlay: false, chainCount: 2),
                CreateGroup(passIndex: 10, targetIdentity: 0, dynamicOverlay: true, chainCount: 1),
            });
        CommandChainSchedule invalid = new(
            structuralSignature: 0x101,
            resourcePlanRevision: 0x200,
            groups: new[]
            {
                CreateGroup(passIndex: 10, targetIdentity: 0, dynamicOverlay: true, chainCount: 1),
                CreateGroup(passIndex: 0, targetIdentity: 0, dynamicOverlay: false, chainCount: 2),
            });

        Should.NotThrow(() => VulkanRenderer.ValidatePrimaryCommandChainSchedule(valid, [firstStatic, secondStatic], dynamicOverlayOpCount: 1));
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidatePrimaryCommandChainSchedule(invalid, [firstStatic, secondStatic], dynamicOverlayOpCount: 1))
            .Message.ShouldContain("dynamic overlay group before");
    }

    [Test]
    public void ValidateCommandChainViewSpecialization_RequiresVrOrderingAndShadowIdentity()
    {
        RenderViewKey leftEye = new(1, 2, VulkanRenderer.CommandChainLeftEyeViewIndex, RenderViewKind.VREye, 0, -1);
        RenderViewKey rightEye = leftEye with { ViewIndex = VulkanRenderer.CommandChainRightEyeViewIndex };
        RenderViewKey multiviewEye = leftEye with { ViewIndex = VulkanRenderer.CommandChainStereoMultiviewViewIndex };
        CommandChainSchedule validVr = new(
            structuralSignature: 0x100,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroupForKeys(new CommandChainKey(0, leftEye, 0, 0, false, 0), new CommandChainKey(0, rightEye, 0, 0, false, 1)) });
        CommandChainSchedule validMultiviewVr = new(
            structuralSignature: 0x101,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroupForKeys(new CommandChainKey(0, multiviewEye, 0, 0, false, 0)) });
        CommandChainSchedule invalidVr = new(
            structuralSignature: 0x102,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroupForKeys(new CommandChainKey(0, rightEye, 0, 0, false, 0), new CommandChainKey(0, leftEye, 0, 0, false, 1)) });
        CommandChainSchedule invalidMixedVr = new(
            structuralSignature: 0x103,
            resourcePlanRevision: 0x200,
            groups: new[]
            {
                CreateGroupForKeys(new CommandChainKey(0, multiviewEye, 0, 0, false, 0)),
                CreateGroupForKeys(new CommandChainKey(0, leftEye, 0, 0, false, 1), new CommandChainKey(0, rightEye, 0, 0, false, 2)),
            });
        RenderViewKey invalidShadow = new(1, 2, 0, RenderViewKind.Shadow, 0, -1);
        CommandChainSchedule invalidShadowSchedule = new(
            structuralSignature: 0x104,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroupForKeys(new CommandChainKey(0, invalidShadow, 0, 0, false, 0)) });

        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainViewSpecialization(validVr));
        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainViewSpecialization(validMultiviewVr));
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainViewSpecialization(invalidVr))
            .Message.ShouldContain("left eye before right eye");
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainViewSpecialization(invalidMixedVr))
            .Message.ShouldContain("mixes");
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainViewSpecialization(invalidShadowSchedule))
            .Message.ShouldContain("shadow key");
    }

    [Test]
    public void BuildCommandChainQueueSchedule_DefaultsToSingleGraphicsFallback()
    {
        CommandChainSchedule commandSchedule = CreateSchedule(dynamicOverlay: false, chainCount: 2);
        CommandChainQueueSchedule queueSchedule = VulkanRenderer.BuildCommandChainQueueSchedule(
            commandSchedule,
            multiQueueRequested: true,
            hasSecondaryGraphicsQueue: true,
            hasAsyncComputeQueue: true,
            hasTransferQueue: true);

        queueSchedule.MultiQueueEnabled.ShouldBeFalse();
        queueSchedule.SingleQueueFallbackAvailable.ShouldBeTrue();
        queueSchedule.Nodes.Length.ShouldBe(1);
        queueSchedule.Nodes.Span[0].QueueKind.ShouldBe(CommandChainQueueKind.Graphics);
        queueSchedule.Nodes.Span[0].GroupIndices.Length.ShouldBe(commandSchedule.Groups.Length);
        queueSchedule.Diagnostics.ShouldContain("graphics queue fallback");
    }

    [Test]
    public void IdentifyCommandChainQueueEligibility_FindsSidecarCandidatesWithoutEnablingThem()
    {
        RenderPassChainGroup computeGroup = CreateGroupForKeys(
            new CommandChainKey(0, new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1), 0, 3, false, 0),
            new CommandChainKey(0, new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1), 0, 3, false, 1));
        computeGroup = new RenderPassChainGroup(
            computeGroup.PassIndex,
            computeGroup.TargetIdentity,
            "SkinComputeTarget",
            computeGroup.ChainKeys,
            computeGroup.StructuralSignature,
            computeGroup.SupportsSecondaryCommandBuffers,
            computeGroup.DynamicOverlay);

        CommandChainQueueEligibility eligibility = VulkanRenderer.IdentifyCommandChainQueueEligibility(computeGroup);

        eligibility.HasFlag(CommandChainQueueEligibility.Graphics).ShouldBeTrue();
        eligibility.HasFlag(CommandChainQueueEligibility.SecondaryGraphics).ShouldBeTrue();
        eligibility.HasFlag(CommandChainQueueEligibility.Compute).ShouldBeTrue();
    }

    [Test]
    public void ValidateCommandChainQueueSchedule_RequiresFallbackAndSidecarTimelineDependencies()
    {
        CommandChainQueueNode graphics = new(
            CommandChainQueueKind.Graphics,
            CommandChainQueueEligibility.Graphics,
            new[] { 0 },
            timelineWaitValue: 0,
            timelineSignalValue: 0,
            diagnosticLabel: "graphics");
        CommandChainQueueNode computeMissingTimeline = new(
            CommandChainQueueKind.Compute,
            CommandChainQueueEligibility.Compute,
            new[] { 1 },
            timelineWaitValue: 0,
            timelineSignalValue: 0,
            diagnosticLabel: "compute");
        CommandChainQueueNode compute = new(
            CommandChainQueueKind.Compute,
            CommandChainQueueEligibility.Compute,
            new[] { 1 },
            timelineWaitValue: 1,
            timelineSignalValue: 2,
            diagnosticLabel: "compute");
        CommandChainQueueDependency dependency = new(
            SourceNodeIndex: 1,
            DestinationNodeIndex: 0,
            TimelineSignalValue: 2,
            RequiresQueueFamilyOwnershipTransfer: true);

        CommandChainQueueSchedule missingFallback = new(
            multiQueueEnabled: false,
            singleQueueFallbackAvailable: false,
            nodes: new[] { graphics },
            dependencies: ReadOnlyMemory<CommandChainQueueDependency>.Empty,
            diagnostics: "bad");
        CommandChainQueueSchedule missingTimeline = new(
            multiQueueEnabled: true,
            singleQueueFallbackAvailable: true,
            nodes: new[] { graphics, computeMissingTimeline },
            dependencies: new[] { dependency },
            diagnostics: "bad");
        CommandChainQueueSchedule valid = new(
            multiQueueEnabled: true,
            singleQueueFallbackAvailable: true,
            nodes: new[] { graphics, compute },
            dependencies: new[] { dependency },
            diagnostics: "ok");

        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainQueueSchedule(missingFallback))
            .Message.ShouldContain("single-queue fallback");
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainQueueSchedule(missingTimeline))
            .Message.ShouldContain("timeline semaphore");
        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainQueueSchedule(valid));
    }

    [Test]
    public void ResolveCommandChainRecordingWorkerCount_HonorsSingleThreadAndDisableFlags()
    {
        VulkanRenderer.ResolveCommandChainRecordingWorkerCount(
                independentChainCount: 128,
                processorCount: 16,
                singleThread: true,
                parallelDisabled: false)
            .ShouldBe(1);

        VulkanRenderer.ResolveCommandChainRecordingWorkerCount(
                independentChainCount: 128,
                processorCount: 16,
                singleThread: false,
                parallelDisabled: true)
            .ShouldBe(1);
    }

    [Test]
    public void ResolveCommandChainRecordingWorkerCount_IsBoundedAndLeavesProcessorForRenderThread()
    {
        VulkanRenderer.ResolveCommandChainRecordingWorkerCount(
                independentChainCount: 128,
                processorCount: 16,
                singleThread: false,
                parallelDisabled: false)
            .ShouldBe(8);

        VulkanRenderer.ResolveCommandChainRecordingWorkerCount(
                independentChainCount: 3,
                processorCount: 16,
                singleThread: false,
                parallelDisabled: false)
            .ShouldBe(3);

        VulkanRenderer.ResolveCommandChainRecordingWorkerCount(
                independentChainCount: 128,
                processorCount: 2,
                singleThread: false,
                parallelDisabled: false)
            .ShouldBe(1);
    }

    private static CommandChain CreateRecordedChain()
    {
        RenderPacket packet = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly);
        return CreateRecordedChain(packet);
    }

    private static CommandChain CreateRecordedChain(RenderPacket packet)
    {
        CommandChain chain = new(new CommandChainKey(0, new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1), 3, 4, false, 5))
        {
            State = CommandChainState.Recorded,
            StructuralSignature = packet.StructuralSignature,
            FrameDataSignature = packet.FrameDataSignature,
            ResourcePlanRevision = packet.ResourcePlanSnapshot.Revision,
            PhysicalImageSignature = packet.ResourcePlanSnapshot.PhysicalImageSignature,
            FramebufferSignature = packet.ResourcePlanSnapshot.FramebufferSignature,
            DescriptorGeneration = packet.DescriptorSnapshot.DescriptorGeneration,
            PipelineGeneration = packet.ResourcePlanSnapshot.PipelineGeneration,
            DrawCount = packet.DrawCount,
            DispatchCount = packet.DispatchCount,
            InstanceCountSignature = VulkanRenderer.ComputePacketInstanceCountSignature(packet),
            DescriptorSetCount = packet.DescriptorSnapshot.DescriptorSetCount,
            DescriptorSetSignature = packet.DescriptorSnapshot.DescriptorSetSignature,
        };

        return chain;
    }

    private static RenderPacket CreatePacket(
        ulong structuralSignature,
        ulong frameDataSignature,
        ulong resourcePlanRevision,
        ulong descriptorGeneration,
        ulong pipelineGeneration,
        RenderPacketVolatility volatility,
        int? descriptorSetCount = null,
        ulong? descriptorSetSignature = null,
        ulong physicalImageSignature = 0x123,
        ulong framebufferSignature = 0x456,
        DrawPacket[]? draws = null,
        DispatchPacket[]? dispatches = null)
        => new(
            viewKey: new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1),
            passIndex: 3,
            targetIdentity: 4,
            targetName: "Target",
            volatility,
            draws: draws is null ? ReadOnlyMemory<DrawPacket>.Empty : new ReadOnlyMemory<DrawPacket>(draws),
            dispatches: dispatches is null ? ReadOnlyMemory<DispatchPacket>.Empty : new ReadOnlyMemory<DispatchPacket>(dispatches),
            descriptorSnapshot: new DescriptorBindingSnapshot(
                descriptorGeneration,
                descriptorSetCount ?? (descriptorGeneration == 0 ? 0 : 1),
                descriptorSetSignature ?? descriptorGeneration),
            resourcePlanSnapshot: new ResourcePlanSnapshot(resourcePlanRevision, physicalImageSignature, framebufferSignature, pipelineGeneration),
            structuralSignature,
            frameDataSignature,
            sourceStartIndex: 5,
            sourceCount: 1,
            dynamicOverlay: false);

    private static CommandChainSchedule CreateSchedule(bool dynamicOverlay, int chainCount)
        => new(
            structuralSignature: dynamicOverlay ? 0x101UL : 0x100UL,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroup(passIndex: dynamicOverlay ? 9 : 3, targetIdentity: 4, dynamicOverlay, chainCount) });

    private static RenderPassChainGroup CreateGroup(int passIndex, int targetIdentity, bool dynamicOverlay, int chainCount)
    {
        CommandChainKey[] keys = new CommandChainKey[chainCount];
        RenderViewKey viewKey = new(1, 2, 0, dynamicOverlay ? RenderViewKind.Overlay : RenderViewKind.Main, 0, -1);
        for (int i = 0; i < keys.Length; i++)
            keys[i] = new CommandChainKey(0, viewKey, passIndex, targetIdentity, dynamicOverlay, i);

        return new RenderPassChainGroup(
            passIndex,
            targetIdentity,
            targetIdentity == 0 ? "<swapchain>" : "Target",
            keys,
            structuralSignature: unchecked(0x500UL + (ulong)chainCount + (dynamicOverlay ? 0x1000UL : 0UL)),
            supportsSecondaryCommandBuffers: true,
            dynamicOverlay);
    }

    private static RenderPassChainGroup CreateGroupForKeys(params CommandChainKey[] keys)
        => new(
            keys.Length == 0 ? 0 : keys[0].PassIndex,
            keys.Length == 0 ? 0 : keys[0].TargetIdentity,
            keys.Length == 0 || keys[0].TargetIdentity == 0 ? "<swapchain>" : "Target",
            keys,
            structuralSignature: unchecked(0x600UL + (ulong)keys.Length),
            supportsSecondaryCommandBuffers: true,
            dynamicOverlay: false);

    private static VulkanRenderer.MeshDrawOp CreateMeshDrawOp(
        VulkanRenderer.PendingMeshDraw draw,
        int passIndex = 0,
        VulkanRenderer.FrameOpContext? context = null)
        => new(
            passIndex,
            Target: null,
            draw,
            context ?? CreateFrameOpContext());

    private static VulkanRenderer.ComputeDispatchOp CreateComputeDispatchOp(
        int passIndex = 0,
        VulkanRenderer.FrameOpContext? context = null)
        => new(
            passIndex,
            Program: null!,
            GroupsX: 1,
            GroupsY: 1,
            GroupsZ: 1,
            Snapshot: new VulkanRenderer.ComputeDispatchSnapshot(
                new Dictionary<string, VulkanRenderer.ProgramUniformValue>(),
                new Dictionary<uint, XRTexture>(),
                new Dictionary<uint, string>(),
                new Dictionary<string, XRTexture>(),
                new Dictionary<uint, VulkanRenderer.ProgramImageBinding>(),
                new Dictionary<uint, XRDataBuffer>()),
            context ?? CreateFrameOpContext());

    private static DrawPacket CreateDrawPacket(uint instanceCount)
        => new(
            OpIndex: 0,
            RendererIdentity: 1,
            MeshIdentity: 2,
            MaterialIdentity: 3,
            ProgramIdentity: 4,
            InstanceCount: instanceCount,
            Transparent: false,
            StructuralSignature: 0x10,
            FrameDataSignature: 0x20);

    private static VulkanRenderer.ClearOp CreateClearOp(int passIndex)
        => new(
            PassIndex: passIndex,
            Target: null,
            ClearColor: true,
            ClearDepth: true,
            ClearStencil: false,
            Color: default,
            Depth: 1.0f,
            Stencil: 0,
            Rect: default,
            Context: CreateFrameOpContext());

    private static VulkanRenderer.FrameOpContext CreateFrameOpContext(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata = null)
        => new(
            PipelineIdentity: 1,
            ViewportIdentity: 2,
            PipelineInstance: null,
            ResourceRegistry: null,
            PassMetadata: passMetadata,
            DisplayWidth: 1920,
            DisplayHeight: 1080,
            InternalWidth: 1920,
            InternalHeight: 1080);

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
