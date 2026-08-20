using System;
using System.Numerics;
using System.Threading;
using ImageMagick;
using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Compute;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;
using XREngine.Rendering.UI;
using XREngine.Scene;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Vulkan renderer composition root.
/// </summary>
public sealed class VulkanRenderer :
    AbstractRenderer<Vk>,
    IIndirectDrawStateBackendCapability,
    IIndirectDrawSecondaryRecordingBackendCapability,
    ISceneDatabaseDeviceAddressBackendCapability,
    IMaterialTableBackendCapability,
    IVulkanVendorUpscaleBackendCapability,
    IOcclusionQueryBackendCapability,
    IRenderTexturePreviewBackendCapability,
    IRenderBackendDiagnosticsCapability,
    IRenderProgramBackendCapability,
    IInteractiveResizePresentationBackendCapability,
    IOpenXrSmokeDiagnosticsBackendCapability,
    ISparseTextureStreamingBackendCapability,
    IStreamlinePresentationBackendCapability,
    IPhysicsChainComputeBackendFactoryCapability
{
    private const int DesktopFramesInFlight = 2;
    private readonly VulkanDeviceContext _deviceContext;
    private readonly VulkanOutputRuntime _outputRuntime;
    private readonly VulkanFrameLoop _frameLoop;
    private readonly VulkanFramePlanner _framePlanner = new();
    private readonly VulkanResourceRuntime _resourceRuntime;
    private readonly VulkanCommandRuntime _commandRuntime = new();
    private readonly VulkanFrameTelemetry _frameTelemetry = new();

    public bool TryGetInteractiveResizePresentationPackage(
        out ulong presentationPackageId,
        out EInteractiveResizeDispatchReason unavailableReason)
    {
        presentationPackageId = 0UL;
        VulkanDesktopOutputState desktop = _outputRuntime.Desktop;
        if (!_deviceContext.IsOperational)
        {
            unavailableReason = EInteractiveResizeDispatchReason.SurfaceUnavailable;
            return false;
        }
        if (Volatile.Read(ref desktop.RecreateInProgress) != 0)
        {
            unavailableReason = EInteractiveResizeDispatchReason.BackendBusy;
            return false;
        }
        if (desktop.Swapchain.Handle == 0 || desktop.Extent.Width == 0 || desktop.Extent.Height == 0)
        {
            unavailableReason = EInteractiveResizeDispatchReason.SurfaceUnavailable;
            return false;
        }
        if (!desktop.PresentScalingActive)
        {
            unavailableReason = EInteractiveResizeDispatchReason.PresentationPackageIncompatible;
            return false;
        }

        uint imageIndex = desktop.LastPresentedImageIndex;
        bool[]? validContent = desktop.ImageHasValidPresentedContent;
        bool[]? everPresented = desktop.ImageEverPresented;
        ulong frameNumber = desktop.LastPresentedFrameNumber;
        if (frameNumber == 0UL ||
            validContent is null || imageIndex >= validContent.Length || !validContent[imageIndex] ||
            everPresented is null || imageIndex >= everPresented.Length || !everPresented[imageIndex])
        {
            unavailableReason = EInteractiveResizeDispatchReason.PresentationPackageUnavailable;
            return false;
        }

        presentationPackageId = frameNumber;
        unavailableReason = EInteractiveResizeDispatchReason.None;
        return true;
    }

    public VulkanRenderer(RendererHostContext hostContext)
        : base(hostContext)
    {
        IVulkanRendererTargetDriver targetDriver = VulkanRendererTargetDriverFactory.Create(hostContext);
        int frameSlotCount = targetDriver is IVulkanExplicitFrameTargetDriver explicitTarget
            ? checked((int)explicitTarget.OutputProperties.FrameSlotCount)
            : DesktopFramesInFlight;
        _resourceRuntime = new VulkanResourceRuntime(frameSlotCount);
        _outputRuntime = new VulkanOutputRuntime(VulkanTargetPolicySnapshot.Capture(targetDriver));
        _deviceContext = new VulkanDeviceContext(
            new VulkanDeviceContextConfiguration(
                targetDriver.RequiresPresentQueue,
                targetDriver.RequiresSwapchainOutput,
                targetDriver.RequiredDeviceExtensions,
                VulkanDeviceContext.DefaultOptionalDeviceExtensions));
        _commandRuntime.ConfigurePrimaryRecording(
            _deviceContext,
            _resourceRuntime,
            _frameTelemetry);
        _resourceRuntime.DescriptorLifetime.Configure(_deviceContext);
        _resourceRuntime.FallbackTexture.Configure(_commandRuntime);
        _resourceRuntime.BlackFallbackTexture.Configure(_commandRuntime);
        _frameLoop = new VulkanFrameLoop(
            Api,
            _deviceContext,
            _outputRuntime,
            _framePlanner,
            _resourceRuntime,
            _commandRuntime,
            _frameTelemetry,
            targetDriver,
            hostContext.TryGetDesktopWindowHost(out IRuntimeRenderWindowHost? windowHost) &&
            windowHost is XRWindow desktopWindow
                ? desktopWindow.Window
                : null);
        VulkanBackendObjectContext backendObjectContext = _resourceRuntime.GetOrCreateBackendObjectContext(
            Api!,
            _deviceContext);
        VulkanBackendObjectFactory.ConfigureDeviceServices(
            backendObjectContext,
            _deviceContext,
            _commandRuntime,
            _framePlanner,
            _frameTelemetry,
            AllowSynchronousResourceUploads);
        VulkanBackendObjectFactory.ConfigureMeshServices(
            _resourceRuntime.WrapperColdComposition,
            _frameLoop.MeshOperationRequests,
            new VulkanFinalPresentationDescriptorPort(
                _outputRuntime.PresentationSource.Publication,
                _resourceRuntime,
                _commandRuntime,
                _frameTelemetry._finalPresentationLedger,
                _frameLoop.CaptureActivity));
        _framePlanner.PublishResourcePlannerGeneration(
            new ResourcePlannerRuntimeGeneration(ResourcePlannerRuntimeState.CreateEmpty()));
        InitializeRenderObjectCache();
    }

    /// <summary>Executes one frame through the composed frame-loop authority.</summary>
    protected override void RenderFrameCallback(double delta)
        => _frameLoop.Render(delta);

    public override void Initialize() => _frameLoop.Initialize();

    /// <summary>
    /// Stops every Vulkan submission producer before XRWindow establishes the
    /// device-idle boundary used by reverse-order teardown.
    /// </summary>
    protected override void OnBackendRetirementBeginning()
        => _frameLoop.BeginBackendRetirement();

    public override void CleanUp()
    {
        try { _frameLoop.CleanUp(waitForGpu: true, gpuIdleAlreadyEstablished: false); }
        finally { DisposeNativeApi(); }
    }

    public override void CleanUpAfterGpuIdle()
    {
        try { _frameLoop.CleanUp(waitForGpu: false, gpuIdleAlreadyEstablished: true); }
        finally { DisposeNativeApi(); }
    }

    internal Vk VulkanApi => Api!;
    internal string TargetDriverName => _outputRuntime.TargetPolicy.DriverName;
    internal bool TargetRequiresPresentQueue => _outputRuntime.TargetPolicy.RequiresPresentQueue;
    internal bool TargetRequiresSwapchainOutput => _outputRuntime.TargetPolicy.RequiresSwapchainOutput;
    internal bool HasInitializedMemoryAllocator => _resourceRuntime.Allocations.Buffers.MemoryAllocator is not null;
    internal bool HasExplicitFrameTarget => _outputRuntime.TargetPolicy.HasExplicitFrameTarget;
    internal bool ExplicitTargetIsDeviceLost => _frameLoop.RequireExplicitFrameTarget().IsDeviceLost;
    internal RenderTargetOutputProperties ExplicitTargetOutputProperties => _frameLoop.RequireExplicitFrameTarget().OutputProperties;
    internal ulong ExplicitTargetGeneration => _frameLoop.RequireExplicitFrameTarget().TargetGeneration;
    internal double ExplicitTargetLastCompletedGpuFrameNanoseconds => _frameLoop.RequireExplicitFrameTarget().LastCompletedGpuFrameNanoseconds;
    internal string ExplicitTargetPresentationDescription => _frameLoop.RequireExplicitFrameTarget().PresentationDescription;
    internal IReadOnlyList<string> EnabledInstanceExtensions => _deviceContext.EnabledInstanceExtensions;
    internal PhysicalDevice PhysicalDevice => _deviceContext.PhysicalDevice;
    public bool StreamlineFrameGenerationSwapchainActive
        => _outputRuntime.Desktop.StreamlineFrameGenerationActive;
    public bool SwapchainRequiresSrgbEncoding
        => _outputRuntime.Desktop.ImageFormat is Format.B8G8R8A8Srgb or Format.R8G8B8A8Srgb;

    public SparseTextureStreamingSupport GetSparseTextureStreamingSupport(
        ESizedInternalFormat format)
        => _resourceRuntime.SparseTextureStreaming.GetSupport(format);

    public bool TryScheduleSparseTextureStreamingTransitionAsync(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError = null)
        => _resourceRuntime.SparseTextureStreaming.TryScheduleTransitionAsync(
            texture,
            request,
            cancellationToken,
            onCompleted,
            onError);

    public SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult result)
        => _resourceRuntime.SparseTextureStreaming.FinalizeTransition(texture, request, result);

    private VulkanBindlessMaterialTextureTableState BindlessMaterialTextureTableState
        => _resourceRuntime.Descriptors.BindlessMaterialTextures;

    internal bool UseDynamicRenderingRenderTargets
        => _deviceContext.MutableCapabilities._useDynamicRenderingRenderTargets;

    public EVulkanRenderTargetMode RequestedRenderTargetMode
        => _outputRuntime._requestedRenderTargetMode;

    public EVulkanRenderTargetMode EffectiveRenderTargetMode
        => UseDynamicRenderingRenderTargets
            ? EVulkanRenderTargetMode.DynamicRendering
            : EVulkanRenderTargetMode.LegacyRenderPass;

    internal VulkanFrameLoop OpenXrFrameLoop => _frameLoop;

    public override bool IsRenderingExternalSwapchainTarget
        => _frameLoop.IsRenderingExternalSwapchainTarget;

    public override bool AllowSynchronousResourceUploads
        => _frameLoop.AllowSynchronousResourceUploads;

    public override bool TryGetExternalSwapchainTargetRegion(out BoundingRectangle region)
        => _frameLoop.TryGetExternalSwapchainTargetRegion(out region);

    public override bool SupportsGpuAutoExposure => _frameLoop.SupportsGpuAutoExposure;

    public override bool UpdateAutoExposureGpu(
        XRTexture sourceTex,
        XRTexture2D exposureTex,
        ColorGradingSettings settings,
        float deltaTime,
        bool generateMipmapsNow)
        => _frameLoop.UpdateAutoExposureGpu(
            sourceTex,
            exposureTex,
            settings,
            deltaTime,
            generateMipmapsNow);

    bool IPhysicsChainComputeBackendFactoryCapability.TryCreatePhysicsChainComputeBackend(
        out IPhysicsChainComputeBackend? backend)
        => VulkanPhysicsChainComputeBackend.TryCreate(this, out backend);

    internal VulkanDeviceContext DeviceContext => _deviceContext;
    public Instance Instance => _deviceContext.Instance;
    public Device Device => _deviceContext.Device;
    public Queue GraphicsQueue => _deviceContext.GraphicsQueue;
    public Queue SecondaryGraphicsQueue => _deviceContext.SecondaryGraphicsQueue;
    public Queue PresentQueue => _deviceContext.PresentQueue;
    public Queue ComputeQueue => _deviceContext.ComputeQueue;
    public Queue TransferQueue => _deviceContext.TransferQueue;
    public IReadOnlyList<string> AvailableDeviceExtensions => _deviceContext.AvailableDeviceExtensions;
    public IReadOnlyList<string> EnabledDeviceExtensions => _deviceContext.EnabledDeviceExtensions;
    public bool HasSecondaryGraphicsQueue => _deviceContext.HasSecondaryGraphicsQueue;

    public override void Blit(XRFrameBuffer? inFBO, XRFrameBuffer? outFBO, int inX, int inY, uint inW, uint inH, int outX, int outY, uint outW, uint outH, EReadBufferMode readBufferMode, bool colorBit, bool depthBit, bool stencilBit, bool linearFilter)
        => _frameLoop.Blit(inFBO, outFBO, inX, inY, inW, inH, outX, outY, outW, outH, readBufferMode, colorBit, depthBit, stencilBit, linearFilter);

    public override void BlitWithDrawBuffer(XRFrameBuffer? inFBO, XRFrameBuffer? outFBO, uint inW, uint inH, uint outW, uint outH, EReadBufferMode readBufferMode, EReadBufferMode drawBufferMode, bool colorBit, bool depthBit, bool stencilBit, bool linearFilter)
        => _frameLoop.BlitWithDrawBuffer(inFBO, outFBO, inW, inH, outW, outH, readBufferMode, drawBufferMode, colorBit, depthBit, stencilBit, linearFilter);

    public (Buffer stagingBuffer, DeviceMemory stagingMemory) CreateBuffer(ulong bufferSize, BufferUsageFlags usage, MemoryPropertyFlags properties, VoidPtr data, bool enableDeviceAddress = false)
        => _commandRuntime.CreateBuffer(bufferSize, usage, properties, data, enableDeviceAddress);

    public void DestroyBuffer(Buffer? buffer, DeviceMemory? memory) => _commandRuntime.DestroyBuffer(buffer, memory);
    public bool CopyBuffer(Buffer? stagingBuffer, Buffer? deviceBuffer, ulong bufferSize) => _commandRuntime.CopyBuffer(stagingBuffer, deviceBuffer, bufferSize);

    public override bool TryQueueScreenshotReadback(BoundingRectangle region, bool withTransparency, Action<ScreenshotReadbackResult> callback, out string? failure)
        => _frameLoop.TryQueueScreenshotReadback(region, withTransparency, callback, out failure);

    public override void PollScreenshotReadbacks() => _frameLoop.PollScreenshotReadbacks();
    public override ScreenshotReadbackStatus GetScreenshotReadbackStatus() => _frameLoop.GetScreenshotReadbackStatus();
    public override void PollGpuRenderStatsReadbacks() => _frameLoop.PollGpuRenderStatsReadbacks();
    public override bool QueueGpuRenderDrawCountReadback(XRDataBuffer drawCountBuffer, uint countByteOffset = 0, uint countElementCount = 1) => _frameLoop.QueueGpuRenderDrawCountReadback(drawCountBuffer, countByteOffset, countElementCount);
    public override bool QueueGpuRenderStatsBufferReadback(XRDataBuffer statsBuffer, bool publishDraws, bool publishTriangles) => _frameLoop.QueueGpuRenderStatsBufferReadback(statsBuffer, publishDraws, publishTriangles);
    public override bool QueueGpuMeshletDispatchDiagnosticsReadback(XRDataBuffer dispatchIndirectBuffer) => _frameLoop.QueueGpuMeshletDispatchDiagnosticsReadback(dispatchIndirectBuffer);
    public override void CalcDotLuminanceAsync(XRTexture2D texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true) => _frameLoop.CalcDotLuminanceAsync(texture, callback, luminance, genMipmapsNow);
    public override void CalcDotLuminanceAsync(XRTexture2DArray texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true) => _frameLoop.CalcDotLuminanceAsync(texture, callback, luminance, genMipmapsNow);
    public override void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback) => _frameLoop.CalcDotLuminanceFrontAsyncCompute(region, withTransparency, luminance, callback);
    public override void CalcDotLuminanceFrontAsync(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback) => _frameLoop.CalcDotLuminanceFrontAsync(region, withTransparency, luminance, callback);
    public override float GetDepth(int x, int y) => _frameLoop.GetDepth(x, y);
    public override void GetDepthAsync(XRFrameBuffer fbo, int x, int y, Action<float> depthCallback) => _frameLoop.GetDepthAsync(fbo, x, y, depthCallback);
    public override void GetPixelAsync(int x, int y, bool withTransparency, Action<ColorF4> colorCallback) => _frameLoop.GetPixelAsync(x, y, withTransparency, colorCallback);
    public override bool ScreenshotRequiresVerticalFlip => _frameLoop.ScreenshotRequiresVerticalFlip;
    public override void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<MagickImage, int> imageCallback) => _frameLoop.GetScreenshotAsync(region, withTransparency, imageCallback);
    public override bool CalcDotLuminance(XRTexture2DArray texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow) => _frameLoop.CalcDotLuminance(texture, luminance, out dotLuminance, genMipmapsNow);
    public override bool CalcDotLuminance(XRTexture2D texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow) => _frameLoop.CalcDotLuminance(texture, luminance, out dotLuminance, genMipmapsNow);
    public override bool TryReadTextureMipRgbaFloat(XRTexture texture, int mipLevel, int layerIndex, out float[]? rgbaFloats, out int width, out int height, out string failure) => _frameLoop.TryReadTextureMipRgbaFloat(texture, mipLevel, layerIndex, out rgbaFloats, out width, out height, out failure);
    public override bool TryReadTexturePixelRgbaFloat(XRTexture texture, int mipLevel, int layerIndex, out Vector4 rgba, out string failure) => _frameLoop.TryReadTexturePixelRgbaFloat(texture, mipLevel, layerIndex, out rgba, out failure);

    protected override AbstractRenderAPIObject CreateAPIRenderObject(GenericRenderObject renderObject) => _resourceRuntime.CreateAPIRenderObject(renderObject);
    public override AdvancedRenderPipelineCapabilities GetAdvancedRenderPipelineCapabilities() => _commandRuntime.GetAdvancedRenderPipelineCapabilities();
    public bool TryBeginOrderedComputeBatch() => _frameLoop.TryBeginOrderedComputeBatch();
    public void CommitOrderedComputeBatch() => _frameLoop.CommitOrderedComputeBatch();
    public void RollbackOrderedComputeBatch() => _frameLoop.RollbackOrderedComputeBatch();

    public override void MemoryBarrier(EMemoryBarrierMask mask) => _frameLoop.EnqueueMemoryBarrier(mask);
    public override void PublishFrameBufferAttachmentsForSampling(XRFrameBuffer frameBuffer) => _frameLoop.PublishFrameBufferAttachmentsForSampling(frameBuffer);
    public override void ApplyRenderParameters(RenderingParameters parameters) => _commandRuntime.ApplyRenderParameters(parameters);
    public override void SetEngineUniforms(XRRenderProgram program, XRCamera camera) => _commandRuntime.SetEngineUniforms(program, camera);
    public override void SetMaterialUniforms(XRMaterial material, XRRenderProgram program) => _commandRuntime.SetMaterialUniforms(material, program);
    public override void ColorMask(bool red, bool green, bool blue, bool alpha) => _commandRuntime.SetColorMask(red, green, blue, alpha);
    public override void ClearColor(ColorF4 color) => _commandRuntime.SetClearColor(color);
    public override void CropRenderArea(BoundingRectangle region) => _commandRuntime.SetScissor(region);
    public override void SetRenderArea(BoundingRectangle region) => _commandRuntime.SetViewport(region);
    public override void ClearRenderArea() => _commandRuntime.ClearViewport();
    public override bool SetIndexedViewportScissors(ReadOnlySpan<BoundingRectangle> viewports, ReadOnlySpan<BoundingRectangle> scissors) => _commandRuntime.TrySetIndexedViewportScissors(viewports, scissors);
    public override void ClearIndexedViewportScissors(int count) => _commandRuntime.ClearIndexedViewportScissorsIfAny(count);

    public bool EnsureQueryGenerated(XRRenderQuery query) => _commandRuntime.EnsureQueryGenerated(GenericToAPI<VkRenderQuery>(query));
    public bool BeginOcclusionQuery(XRRenderQuery query) => _frameLoop.TryEnqueueQueryOperation(query, ERenderQueryOperation.Begin);
    public bool EndOcclusionQuery(XRRenderQuery query) => _frameLoop.TryEnqueueQueryOperation(query, ERenderQueryOperation.End);
    public ERenderQueryReadStatus WriteTimestamp(XRRenderQuery query) => _frameLoop.TryWriteTimestamp(query);
    public ERenderQueryReadStatus TryGetTimestamp(XRRenderQuery query, out TimestampQueryResult result) => _commandRuntime.TryGetTimestamp(GenericToAPI<VkRenderQuery>(query), out result);
    public ERenderQueryReadStatus TryGetAnySamplesPassed(XRRenderQuery query, out OcclusionQueryResult result, in RenderQueryTicket expectedTicket = default) => _commandRuntime.TryGetAnySamplesPassed(GenericToAPI<VkRenderQuery>(query), out result, expectedTicket);
    public RenderQueryTicket GetTicket(XRRenderQuery query) => _commandRuntime.GetQueryTicket(GenericToAPI<VkRenderQuery>(query));

    public override void BindVAOForRenderer(XRMeshRenderer.BaseVersion? version) => _commandRuntime.BindIndirectMesh(version is null ? null : GenericToAPI<VkMeshRenderer>(version));
    public override bool ValidateIndexedVAO(XRMeshRenderer.BaseVersion? version) => _commandRuntime.ValidateIndirectIndexedMesh(version is null ? null : GenericToAPI<VkMeshRenderer>(version));
    public override bool TryGetIndexBufferInfo(XRMeshRenderer.BaseVersion? version, out IndexSize indexElementSize, out uint indexCount) => _commandRuntime.TryGetIndirectIndexBufferInfo(version is null ? null : GenericToAPI<VkMeshRenderer>(version), out indexElementSize, out indexCount);
    public override bool TrySyncMeshRendererIndexBuffer(XRMeshRenderer meshRenderer, XRDataBuffer indexBuffer, IndexSize elementSize) => _commandRuntime.TrySyncIndirectIndexBuffer(meshRenderer, indexBuffer, elementSize);
    public override void BindDrawIndirectBuffer(XRDataBuffer buffer) => _commandRuntime.BindIndirectBuffer(GenericToAPI<VkDataBuffer>(buffer));
    public override void UnbindDrawIndirectBuffer() => _commandRuntime.BindIndirectBuffer(null);
    public override void BindParameterBuffer(XRDataBuffer buffer) => _commandRuntime.BindIndirectCountBuffer(GenericToAPI<VkDataBuffer>(buffer));
    public override void UnbindParameterBuffer() => _commandRuntime.BindIndirectCountBuffer(null);
    public override void MultiDrawElementsIndirect(uint drawCount, uint stride) => _frameLoop.EnqueueIndirectDraw("MultiDrawElementsIndirectWithOffset", drawCount, stride, 0);
    public override void MultiDrawElementsIndirectWithOffset(uint drawCount, uint stride, nuint byteOffset) => _frameLoop.EnqueueIndirectDraw("MultiDrawElementsIndirectWithOffset", drawCount, stride, byteOffset);
    public override void MultiDrawElementsIndirectCount(uint maxDrawCount, uint stride, nuint byteOffset, nuint countByteOffset) => _frameLoop.EnqueueIndirectCountDraw(maxDrawCount, stride, byteOffset, countByteOffset);
    public override bool SupportsIndirectCountDraw() => _commandRuntime.SupportsIndirectCountDraw();
    public override void ConfigureVAOAttributesForProgram(XRRenderProgram program, XRMeshRenderer.BaseVersion? version) => _commandRuntime.ConfigureIndirectVertexInput(program, version);
    bool IIndirectDrawStateBackendCapability.TryBeginIndirectDrawState(XRRenderProgram program, XRMaterial? material, in Matrix4x4 modelMatrix, out IndirectDrawStateToken token) => _commandRuntime.TryBeginIndirectDrawState(program, material, modelMatrix, out token);
    void IIndirectDrawStateBackendCapability.EndIndirectDrawState(in IndirectDrawStateToken token) => _commandRuntime.EndIndirectDrawState(token);
    bool IIndirectDrawSecondaryRecordingBackendCapability.TryBeginProducerCompleteIndirectStream(XRDataBuffer indirectBuffer, XRDataBuffer? parameterBuffer, out IndirectDrawSecondaryRecordingToken token) => _commandRuntime.TryBeginProducerCompleteIndirectStream(GenericToAPI<VkDataBuffer>(indirectBuffer), parameterBuffer is null ? null : GenericToAPI<VkDataBuffer>(parameterBuffer), indirectBuffer, parameterBuffer, out token);
    void IIndirectDrawSecondaryRecordingBackendCapability.EndProducerCompleteIndirectStream(in IndirectDrawSecondaryRecordingToken token) => _commandRuntime.EndProducerCompleteIndirectStream(token);
    bool ISceneDatabaseDeviceAddressBackendCapability.TryBindSceneDatabaseDeviceAddressUniforms(XRRenderProgram program, XRDataBuffer drawMetadataBuffer, XRDataBuffer? instanceTransformBuffer, bool useInstanceTransformBuffer, string consumer) => _resourceRuntime.TryBindSceneDatabaseDeviceAddressUniforms(program, drawMetadataBuffer, instanceTransformBuffer, useInstanceTransformBuffer, consumer);

    protected override bool SupportsImGui => true;
    protected override IImGuiRendererBackend? GetImGuiBackend(XRViewport? viewport) => SupportsImGui ? _frameLoop.GetOrCreateImGuiBackend(XRWindow, ResetImGuiFrameMarker) : null;
    public bool TryGetTexturePreviewHandle(XRTexture texture, in RenderTexturePreviewOptions options, out nint handle, out bool requiresVerticalFlip, out string? failureReason) => _frameLoop.TryGetTexturePreviewHandle(texture, in options, out handle, out requiresVerticalFlip, out failureReason);
    public IReadOnlyList<RenderBackendDiagnosticError> GetTrackedErrors() => Array.Empty<RenderBackendDiagnosticError>();
    bool IRenderProgramBackendCapability.IsProgramReady(XRRenderProgram program)
        => GetOrCreateAPIRenderObject(program) is VkRenderProgram { IsLinked: true };
    string IRenderProgramBackendCapability.DescribeProgramReadiness(XRRenderProgram program)
        => GetOrCreateAPIRenderObject(program) is VkRenderProgram backendProgram
            ? $"linked={backendProgram.IsLinked};stage={program.ShaderMetadata.Backend.Stage};generation={backendProgram.LinkGeneration}"
            : "missing-wrapper";
    void IRenderProgramBackendCapability.LogBackendErrors(string context)
    {
        // Vulkan validation and shader diagnostics are published asynchronously;
        // unlike OpenGL there is no thread-local error queue to drain here.
    }
    object IRenderBackendDiagnosticsCapability.GetLiveImageAllocationDiagnostics(int limit) => _resourceRuntime.GetLiveImageAllocationDiagnostics(limit);
    object IRenderBackendDiagnosticsCapability.GetLastFrameOperationTraceDiagnostics(int limit, string? targetContains) => _commandRuntime.GetLastFrameOpTraceDiagnostics(limit, targetContains);
    object IRenderBackendDiagnosticsCapability.GetFinalPresentationLedgerDiagnostics(int limit) => _frameLoop.GetFinalPresentationLedgerDiagnostics(limit);
    object IRenderBackendDiagnosticsCapability.ConfigureFinalPresentationLedgerDiagnostics(bool enabled, bool frozen, bool clear) => _frameLoop.ConfigureFinalPresentationLedgerDiagnostics(enabled, frozen, clear);
    bool IRenderBackendDiagnosticsCapability.TryReadDepthPixelDebug(XRFrameBuffer frameBuffer, int x, int y, out object? diagnostic) => _frameLoop.TryReadDepthPixelDebug(frameBuffer, x, y, out diagnostic);
    string IRenderBackendDiagnosticsCapability.EffectiveRenderTargetMode => EffectiveRenderTargetMode.ToString();
    public void ResetDesktopRejectionEvidence(bool injectionRequested) => ResetPhase524bDesktopRejectionEvidence(injectionRequested);
    public OpenXrSmokeDesktopRejectionEvidence CaptureDesktopRejectionEvidence() => CapturePhase524bDesktopRejectionEvidence();

    public IDisposable EnterPipelineResourcePlannerReadbackScope(XRRenderPipelineInstance pipeline, XRViewport? viewport) => _frameLoop.EnterPipelineResourcePlannerReadbackScope(pipeline, viewport);
    internal override IDisposable? EnterRenderPipelineFrameResourceScope(XRRenderPipelineInstance pipeline, XRViewport? viewport) => _frameLoop.EnterRenderPipelineFrameResourceScope(pipeline, viewport);
    internal override bool TryPrepareRenderResourceGeneration(XRRenderPipelineInstance pipeline, RenderResourceGeneration generation, XRViewport? viewport, out IRenderResourceGenerationTransaction? transaction, out string? failureReason) => _frameLoop.TryPrepareRenderResourceGeneration(pipeline, generation, viewport, out transaction, out failureReason);

    internal bool CanUseNvIndirectBufferCopyUploads => _commandRuntime.CanUseNvIndirectBufferCopyUploads;
    internal ulong GetBufferDeviceAddress(Buffer buffer) => _commandRuntime.GetBufferDeviceAddress(buffer);
    public bool TryCopyBufferViaIndirectNv(Buffer source, Buffer destination, ulong size, ulong sourceOffset = 0, ulong destinationOffset = 0) => _commandRuntime.TryCopyBufferViaIndirectNv(source, destination, size, sourceOffset, destinationOffset);
    public bool TryCopyBufferToImageViaIndirectNv(Buffer source, ulong sourceOffset, Image destination, ImageLayout layout, ImageSubresourceLayers subresource, Offset3D offset, Extent3D extent) => _commandRuntime.TryCopyBufferToImageViaIndirectNv(source, sourceOffset, destination, layout, subresource, offset, extent);
    public bool TryCopyMemoryIndirectNv(ulong commandAddress, uint copyCount, uint stride) => _commandRuntime.TryCopyMemoryIndirectNv(commandAddress, copyCount, stride);
    public bool TryCopyMemoryToImageIndirectNv(ulong commandAddress, uint copyCount, uint stride, Image destination, ImageLayout layout, ReadOnlySpan<ImageSubresourceLayers> subresources) => _commandRuntime.TryCopyMemoryToImageIndirectNv(commandAddress, copyCount, stride, destination, layout, subresources);
    public bool TryDecompressBufferGDeflateNv(Buffer source, ulong sourceOffset, ulong compressedSize, Buffer destination, ulong destinationOffset, ulong decompressedSize) => _commandRuntime.TryDecompressBufferGDeflateNv(source, sourceOffset, compressedSize, destination, destinationOffset, decompressedSize);
    public bool TryDecompressMemoryNv(DecompressMemoryRegionNV region) => _commandRuntime.TryDecompressMemoryNv(new ReadOnlySpan<DecompressMemoryRegionNV>(in region));
    public bool TryDecompressMemoryNv(ReadOnlySpan<DecompressMemoryRegionNV> regions) => _commandRuntime.TryDecompressMemoryNv(regions);
    public bool TryDecompressMemoryIndirectCountNv(ulong commandsAddress, ulong countAddress, uint stride) => _commandRuntime.TryDecompressMemoryIndirectCountNv(commandsAddress, countAddress, stride);
    public bool SupportsOrderedComputeWork => _commandRuntime.SupportsOrderedComputeWork;
    public ERendererComputeEnqueueStatus TryDispatchComputeIndirect(XRRenderProgram program, XRDataBuffer arguments, nint byteOffset, string label) => _frameLoop.TryDispatchComputeIndirect(program, arguments, byteOffset, label);
    public ERendererComputeEnqueueStatus TryEnqueueBufferCopy(XRDataBuffer source, nint sourceOffset, XRDataBuffer destination, nint destinationOffset, nuint byteCount, string label) => _frameLoop.TryEnqueueBufferCopy(source, sourceOffset, destination, destinationOffset, byteCount, label);
    public override bool TryEnqueueGpuDiagnosticBufferSnapshot(XRDataBuffer source, XRDataBuffer destination, nuint byteCount, string label)
        => TryEnqueueBufferCopy(source, 0, destination, 0, byteCount, label) == ERendererComputeEnqueueStatus.Enqueued;
    public ERendererComputeEnqueueStatus TryCompleteOrderedComputePass(EMemoryBarrierMask mask, string label) => _frameLoop.TryCompleteOrderedComputePass(mask, label);
    public override XRGpuFence? InsertGpuFence() => _frameLoop.InsertOrderedComputeFence();
    public bool TryEnsureComputeBufferReady(XRDataBuffer buffer) => _commandRuntime.TryEnsureComputeBufferReady(_resourceRuntime.WrapperLookup, buffer, _frameLoop.AllowSynchronousResourceUploads);
    public bool TryReadMappedBuffer(XRDataBuffer buffer, Span<byte> destination) => _commandRuntime.TryReadMappedBuffer(_resourceRuntime.WrapperLookup, buffer, destination);
    public override EMeshShaderDialect MeshShaderDialect => _deviceContext.SupportsMeshTaskIndirectCount ? EMeshShaderDialect.VulkanEXT : EMeshShaderDialect.None;
    public override bool SupportsDirectMeshTaskDispatch() => false;
    public override bool SupportsIndirectCountMeshTaskDispatch() => _deviceContext.SupportsMeshTaskIndirectCount;
    public override bool SupportsProductionMeshletShaders() => _deviceContext.SupportsMeshTaskIndirectCount;
    public override bool TryDrawMeshTasksIndirectCount(XRRenderProgram program, XRDataBuffer indirect, XRDataBuffer count, uint maxDrawCount, uint stride, out string failureReason, nuint byteOffset = 0, nuint countByteOffset = 0)
    {
        if (!ValidateMeshTasksIndirectCountArgs(
                indirect,
                count,
                maxDrawCount,
                stride,
                byteOffset,
                countByteOffset,
                out failureReason))
        {
            return false;
        }

        return _frameLoop.TryDrawMeshTasksIndirectCount(
            program,
            indirect,
            count,
            maxDrawCount,
            stride,
            byteOffset,
            countByteOffset,
            out failureReason);
    }
    public override string MeshletDispatchUnsupportedReason => _frameLoop.GetMeshletDispatchUnsupportedReason();
    public override ERvcDescriptorBackend RvcDescriptorBackend => _commandRuntime.RvcDescriptorBackend;
    public override bool SupportsRvcMaterialResourceTable => _commandRuntime.SupportsRvcMaterialResourceTable;
    public override bool SupportsRvcVisibilityTargets => _commandRuntime.SupportsRvcVisibilityTargets;
    public override bool SupportsRvcOpenXrVisibilityMaskStencil => _commandRuntime.SupportsRvcOpenXrVisibilityMaskStencil;
    public override ERvcVulkanProductionFeature RvcVulkanProductionFeatures => _commandRuntime.ResolveRvcProductionFeatures(RuntimeEngine.Rendering.State.HasVulkanMultiView);
    public bool StreamlineFrameGenerationProvisioned => _outputRuntime.StreamlineFrameGenerationProvisioned;
    internal VulkanStreamlineDeviceBinding StreamlineDeviceBinding => _outputRuntime.CaptureStreamlineDeviceBinding(_deviceContext);
    internal Format SwapchainImageFormat => _outputRuntime.SwapchainImageFormat;
    internal bool StreamlineFrameGenerationSwapchainIncludesDlss => _outputRuntime.StreamlineFrameGenerationSwapchainIncludesDlss;
    private void PrepareStreamlineVulkanRequirements() => _outputRuntime.PrepareStreamlineVulkanRequirements(XRWindow.IsSecondaryGpuContext, _frameTelemetry._diagnosticOptions.RenderDocFriendly);
    private void ValidateStreamlineSelectedPhysicalDevice() => _outputRuntime.ValidateStreamlineSelectedPhysicalDevice(_deviceContext.PhysicalDevice.Handle);
    internal static bool ShouldProvisionOptionalStreamlineFrameGeneration(bool toggles, bool runtimeAvailable, bool supported) => VulkanOutputRuntime.ShouldProvisionOptionalStreamlineFrameGeneration(toggles, runtimeAvailable, supported);
    internal static void ResetPhase524bDesktopRejectionEvidence(bool injectionRequested) => VulkanOutputRuntime.ResetPhase524bDesktopRejectionEvidence(injectionRequested);
    internal static OpenXrSmokeDesktopRejectionEvidence CapturePhase524bDesktopRejectionEvidence() => VulkanOutputRuntime.CapturePhase524bDesktopRejectionEvidence();
    ulong IVulkanVendorUpscaleBackendCapability.FrameIndex => _frameLoop.AcceptedAttemptCount;
    bool IVulkanVendorUpscaleBackendCapability.TryCreateDlssSession(uint viewportId, out IRuntimeVendorUpscaleSession? session, out string failureReason) => _outputRuntime.TryCreateDlssSession(_deviceContext, viewportId, out session, out failureReason);
    bool IVulkanVendorUpscaleBackendCapability.TryCreateFrameGenerationSession(uint viewportId, out IRuntimeVendorUpscaleSession? session, out string failureReason) => _outputRuntime.TryCreateFrameGenerationSession(_deviceContext, viewportId, out session, out failureReason);
    bool IVulkanVendorUpscaleBackendCapability.TryDispatchFrameGeneration(XRViewport viewport, in VulkanUpscaleBridgeDispatchParameters parameters, XRTexture depth, XRTexture motion, XRTexture hudlessColor, out int errorCode, out string? errorMessage) => _frameLoop.TryDispatchFrameGeneration(viewport, parameters, depth, motion, hudlessColor, out errorCode, out errorMessage);
    bool IVulkanVendorUpscaleBackendCapability.TryEnqueueDlssUpscale(int passIndex, IRuntimeVendorUpscaleSession session, XRTexture sourceColor, XRTexture depth, XRTexture motion, XRTexture outputColor, XRTexture? exposure, in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason) => _frameLoop.TryEnqueueDlssUpscale(passIndex, session, sourceColor, depth, motion, outputColor, exposure, parameters, out failureReason);
    bool IVulkanVendorUpscaleBackendCapability.TryEnqueueFrameGeneration(int passIndex, IRuntimeVendorUpscaleSession session, XRTexture depth, XRTexture motion, XRTexture hudlessColor, in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason) => _frameLoop.TryEnqueueFrameGeneration(passIndex, session, depth, motion, hudlessColor, parameters, out failureReason);

    public bool SupportsDeviceFault => _deviceContext.SupportsDeviceFault;
    public bool SupportsDeviceAddressBindingReport => _deviceContext.SupportsDeviceAddressBindingReport;
    public bool SupportsNvDiagnosticCheckpoints => _deviceContext.SupportsNvDiagnosticCheckpoints;
    public bool SupportsNvDiagnosticsConfig => _deviceContext.SupportsNvDiagnosticsConfig;
    public bool SupportsNvMemoryDecompression => _deviceContext.SupportsNvMemoryDecompression;
    public bool SupportsNvCopyMemoryIndirect => _deviceContext.SupportsNvCopyMemoryIndirect;
    public bool SupportsExternalMemoryWin32 => _deviceContext.SupportsExternalMemoryWin32;
    public bool SupportsExternalSemaphoreWin32 => _deviceContext.SupportsExternalSemaphoreWin32;
    public bool SupportsBufferDeviceAddress => _deviceContext.SupportsBufferDeviceAddress;
    bool IMaterialTableBackendCapability.SupportsBufferDeviceAddress => SupportsBufferDeviceAddress;
    bool IMaterialTableBackendCapability.SupportsBindlessMaterialTable
        => _resourceRuntime.Descriptors.BindlessMaterialCapability.Tier >=
           EVulkanBindlessMaterialCapabilityTier.DescriptorIndexingReady;
    bool IMaterialTableBackendCapability.SupportsBindlessTextureHandles => false;
    string IMaterialTableBackendCapability.BindlessMaterialUnavailableReason
    {
        get
        {
            VulkanBindlessMaterialCapability capability = _resourceRuntime.Descriptors.BindlessMaterialCapability;
            return capability.Tier >= EVulkanBindlessMaterialCapabilityTier.DescriptorIndexingReady
                ? string.Empty
                : capability.Reason;
        }
    }
    bool IMaterialTableBackendCapability.TryEnsureMaterialTextureTable(out string reason)
        => _resourceRuntime.Descriptors.TryEnsureGlobalMaterialTextureDescriptorTable(out reason);
    XREngine.Rendering.Materials.MaterialTextureReferenceResolution IMaterialTableBackendCapability.ResolveMaterialTextureReference(
        XRTexture texture,
        string semantic)
        => _resourceRuntime.Descriptors.ResolveMaterialTextureDescriptorReference(texture, semantic);
    void IMaterialTableBackendCapability.FlushMaterialTextureTableUpdates()
        => _resourceRuntime.Descriptors.FlushGlobalMaterialTextureDescriptorUpdates();
    void IMaterialTableBackendCapability.ReleaseMaterialTextureReference(
        in XREngine.Rendering.Materials.GPUMaterialRetiredHandle retired)
    {
        // Vulkan descriptor slots are owned by the global table and age out by
        // their last-used frame. A material-row retirement must not recycle a
        // slot that can still be referenced by another row.
    }
    bool IMaterialTableBackendCapability.BeginGlobalMaterialTextureDescriptorScope(
        XRRenderProgram program,
        string consumer)
        => _resourceRuntime.Descriptors.BeginGlobalMaterialTextureDescriptorScope(program, consumer);
    void IMaterialTableBackendCapability.EndGlobalMaterialTextureDescriptorScope(XRRenderProgram program)
        => _resourceRuntime.Descriptors.EndGlobalMaterialTextureDescriptorScope(program);
    public bool SupportsVulkanMeshTaskIndirectCount => _deviceContext.SupportsMeshTaskIndirectCount;
    public bool SupportsDynamicRendering => _deviceContext.SupportsDynamicRendering;
    public bool SupportsIndexTypeUint8 => _deviceContext.SupportsIndexTypeUint8;
    public bool SupportsSynchronization2 => _deviceContext.SupportsSynchronization2;
    public bool SupportsDepthClipControl => _deviceContext.SupportsDepthClipControl;
    public bool SupportsGraphicsPipelineLibrary => _deviceContext.SupportsGraphicsPipelineLibrary;
    public bool SupportsTransformFeedback => _deviceContext.SupportsTransformFeedback;
    public bool SupportsTransformFeedbackGeometryStreams => _deviceContext.SupportsTransformFeedbackGeometryStreams;
    public bool SupportsTransformFeedbackQueries => _deviceContext.SupportsTransformFeedbackQueries;
    public bool SupportsTransformFeedbackDraw => _deviceContext.SupportsTransformFeedbackDraw;
    public bool SupportsHostQueryReset => _deviceContext.SupportsHostQueryReset;
    public bool SupportsVulkanFragmentShadingRate => _deviceContext.SupportsVulkanFragmentShadingRate;
    public bool SupportsVulkanFragmentShadingRateAttachment => _deviceContext.SupportsVulkanFragmentShadingRateAttachment;
    public PhysicalDeviceFragmentShadingRatePropertiesKHR FragmentShadingRateProperties => _deviceContext.FragmentShadingRateProperties;
    public bool SupportsVulkanFragmentDensityMap => _deviceContext.SupportsVulkanFragmentDensityMap;
    public bool SupportsVulkanFragmentDensityMapDynamic => _deviceContext.SupportsVulkanFragmentDensityMapDynamic;
    public PhysicalDeviceTransformFeedbackPropertiesEXT TransformFeedbackProperties => _deviceContext.TransformFeedbackProperties;
    public bool SupportsFragmentStoresAndAtomics => _deviceContext.SupportsFragmentStoresAndAtomics;
    public bool SupportsVertexPipelineStoresAndAtomics => _deviceContext.SupportsVertexPipelineStoresAndAtomics;
    public bool SupportsGeometryShader => _deviceContext.SupportsGeometryShader;
    public bool SupportsVulkan14 => _deviceContext.SupportsVulkan14;
    public bool SupportsDynamicRenderingLocalRead => _deviceContext.SupportsDynamicRenderingLocalRead;
    public bool SupportsDynamicRenderingLocalReadStorageResources => _deviceContext.SupportsDynamicRenderingLocalReadStorageResources;
    public bool SupportsDynamicRenderingLocalReadColorAttachments => _deviceContext.SupportsDynamicRenderingLocalReadColorAttachments;
    public bool SupportsDynamicRenderingLocalReadDepthStencilAttachments => _deviceContext.SupportsDynamicRenderingLocalReadDepthStencilAttachments;
    public bool SupportsDynamicRenderingLocalReadMultisampledAttachments => _deviceContext.SupportsDynamicRenderingLocalReadMultisampledAttachments;
    public bool SupportsMaintenance4 => _deviceContext.SupportsMaintenance4;
    public bool SupportsMaintenance5 => _deviceContext.SupportsMaintenance5;
    public bool SupportsExtendedFlags => _deviceContext.SupportsExtendedFlags;
    public bool SupportsDescriptorHeap => _deviceContext.SupportsDescriptorHeap;
    public bool SupportsShaderObject => _deviceContext.SupportsShaderObject;
    public bool SupportsMemoryBudget => _deviceContext.SupportsMemoryBudget;
    public bool SupportsMemoryPriority => _deviceContext.SupportsMemoryPriority;
    public bool SupportsAccelerationStructure => _deviceContext.SupportsAccelerationStructure;
    public bool SupportsRayTracingPipeline => _deviceContext.SupportsRayTracingPipeline;
    public bool SupportsRayQuery => _deviceContext.SupportsRayQuery;
    public bool SupportsDeviceGeneratedCommands => _deviceContext.SupportsDeviceGeneratedCommands;
    public MemoryDecompressionMethodFlagsNV NvMemoryDecompressionMethods => _deviceContext.NvMemoryDecompressionMethods;
    public ulong NvMaxMemoryDecompressionIndirectCount => _deviceContext.NvMaxMemoryDecompressionIndirectCount;
    public ulong NvCopyMemoryIndirectSupportedQueues => _deviceContext.NvCopyMemoryIndirectSupportedQueues;

    public override RendererBackendId BackendId => RendererBackendId.Vulkan;
    protected override Vk GetAPI() => Vk.GetApi();
    public override void StencilMask(uint mask) => _commandRuntime.SetStencilMask(mask);
    public override void EnableStencilTest(bool enable) { }
    public override void StencilFunc(EComparison function, int reference, uint mask) { }
    public override void StencilOp(EStencilOp sfail, EStencilOp dpfail, EStencilOp dppass) { }
    public override void EnableBlend(bool enable) { }
    public override void BlendFunc(EBlendingFactor src, EBlendingFactor dst) { }
    public override void BlendFuncSeparate(EBlendingFactor srcRGB, EBlendingFactor dstRGB, EBlendingFactor srcAlpha, EBlendingFactor dstAlpha) { }
    public override void BlendEquation(EBlendEquationMode mode) { }
    public override void BlendEquationSeparate(EBlendEquationMode modeRGB, EBlendEquationMode modeAlpha) { }
    public override void EnableSampleShading(float minValue) { }
    public override void DisableSampleShading() { }
    public override void AllowDepthWrite(bool value) => _commandRuntime.AllowDepthWrite(value);
    public override void ClearDepth(float value) => _commandRuntime.SetClearDepth(value);
    public override void ClearStencil(int value) => _commandRuntime.SetClearStencil(value);
    public override void EnableDepthTest(bool value) => _commandRuntime.EnableDepthTest(value);
    public override void DepthFunc(EComparison comparison) => _commandRuntime.SetDepthCompare(comparison);
    public override void DispatchCompute(XRRenderProgram program, int numGroupsX, int numGroupsY, int numGroupsZ)
        => TryDispatchCompute(program, checked((uint)Math.Max(numGroupsX, 1)), checked((uint)Math.Max(numGroupsY, 1)), checked((uint)Math.Max(numGroupsZ, 1)));
    public override ERendererComputeEnqueueStatus TryDispatchCompute(XRRenderProgram program, uint groupsX, uint groupsY, uint groupsZ)
        => _frameLoop.TryDispatchCompute(program, groupsX, groupsY, groupsZ);
    public override void WaitForGpu() => _frameLoop.WaitForDeviceIdle();
    public override bool TryWaitForGpu(TimeSpan timeout) => _frameLoop.TryWaitForDeviceIdle(timeout);
    public override void SetReadBuffer(EReadBufferMode mode) => _frameLoop.SetReadBuffer(mode);
    public override void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode) => _frameLoop.SetReadBuffer(fbo, mode);
    public override void TrackWindowPresentSource(XRTexture? colorTexture, XRFrameBuffer? sourceFrameBuffer)
        => _frameLoop.TrackWindowPresentSource(colorTexture, sourceFrameBuffer);
    public override RenderTextureSamplingState GetTextureShaderSamplingState(XRTexture? texture)
        => _frameLoop.GetTextureShaderSamplingState(texture);
    public override void BindFrameBuffer(EFramebufferTarget fboTarget, XRFrameBuffer? fbo)
        => _frameLoop.BindFrameBuffer(fboTarget, fbo);
    public override void Clear(bool color, bool depth, bool stencil) => _frameLoop.Clear(color, depth, stencil);
    public override byte GetStencilIndex(float x, float y) => _frameLoop.GetStencilIndex(x, y);
    public override void SetCroppingEnabled(bool enabled) => _commandRuntime.SetCroppingEnabled(enabled);
    public void DeviceWaitIdle() => _frameLoop.WaitForDeviceIdle();
    public bool SupportsMultipleGraphicsQueues() => _deviceContext.HasSecondaryGraphicsQueue;

    /// <summary>Records and submits one frame through an explicit presentation target.</summary>
    internal void SubmitExplicitTargetFrame(Action<Vk, CommandBuffer, VulkanRenderFrameTarget> record)
        => _frameLoop.ExecuteExplicitTargetFrame(record);

    /// <summary>
    /// Acquires an explicit target, lets an ordinary viewport/render pipeline
    /// enqueue its production work, then records and submits that work through
    /// the common Vulkan frame-target path.
    /// </summary>
    internal void SubmitExplicitProductionFrame(
        Action<RenderFrameOutputDescription> buildFrame)
    {
        AbstractRenderer? previous = Current;
        bool previousActive = Active;
        Current = this;
        Active = true;
        try
        {
            _frameLoop.ExecuteExplicitProductionFrame(buildFrame);
        }
        finally
        {
            Active = previousActive;
            Current = previous;
        }
    }

    /// <summary>Reads the last completed explicit-target color output.</summary>
    internal byte[] ReadbackExplicitTargetColor(int maxByteCount, ImageLayout sourceLayout)
        => _frameLoop.RequireExplicitFrameTarget().ReadbackLastSubmittedColor(maxByteCount, sourceLayout);

    /// <summary>Hashes the last completed explicit-target color output.</summary>
    internal string ComputeExplicitTargetColorHash(ImageLayout sourceLayout)
        => _frameLoop.RequireExplicitFrameTarget().ComputeLastSubmittedColorHash(sourceLayout);
}
