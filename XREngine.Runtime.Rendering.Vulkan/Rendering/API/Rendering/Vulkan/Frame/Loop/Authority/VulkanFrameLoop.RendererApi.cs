using System.Threading;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>Renderer API operations whose frame-local inputs are owned by the frame loop.</summary>
internal sealed partial class VulkanFrameLoop
{
    internal void SetReadBuffer(EReadBufferMode mode)
        => _commandRuntime.ActiveReadBufferMode = mode;

    internal void SetReadBuffer(XRFrameBuffer? frameBuffer, EReadBufferMode mode)
    {
        _commandRuntime.ActiveBoundReadFrameBuffer = frameBuffer;
        _commandRuntime.ActiveReadBufferMode = mode;
        if (frameBuffer is not null)
            (GetOrCreateAPIRenderObject(frameBuffer, generateNow: true) as VkFrameBuffer)?.Generate();
    }

    internal void TrackWindowPresentSource(XRTexture? colorTexture, XRFrameBuffer? sourceFrameBuffer)
    {
        FrameOpContext context = CaptureFrameOpContextForCurrentPipelineScope();
        VulkanPresentationSourceTuple source = CreateWindowPresentationSourceTuple(
            colorTexture,
            sourceFrameBuffer,
            in context);
        VulkanPresentationSourceTuple published = _windowPresentSource.PublishLogical(
            in source,
            retainEquivalentCurrentSource: true);

        PublishPresentationSourceCompatibilityFields(in published);
        if (DescriptorTraceEnabled)
            Debug.VulkanEvery(
                $"Vulkan.TrackWindowPresentSource.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] TrackWindowPresentSource: tex='{0}' fbo='{1}' snapReady={2} img=0x{3:X} view=0x{4:X} sampler=0x{5:X} epoch={6} pipeline={7} viewport={8} output={9}",
                colorTexture?.Name ?? "<null>", published.FrameBuffer?.Name ?? "<null>",
                published.Image.Handle != 0,
                published.Image.Handle,
                published.ImageView.Handle,
                published.Sampler.Handle,
                published.LogicalEpoch, context.PipelineIdentity, context.ViewportIdentity, context.OutputTargetIdentity);
    }

    private VulkanPresentationSourceTuple CreateWindowPresentationSourceTuple(
        XRTexture? colorTexture,
        XRFrameBuffer? sourceFrameBuffer,
        in FrameOpContext context,
        bool resolveNativeSource = true,
        IWindowPresentationBindingPublisher? presentationPublisher = null,
        ulong presentationPublicationToken = 0)
    {
        XRFrameBuffer? resolvedFrameBuffer = sourceFrameBuffer ?? ResolveWindowPresentFallbackFrameBuffer(colorTexture);
        VkImageDescriptorSnapshot snapshot = default;
        bool snapshotReady = false;
        if (resolveNativeSource &&
            colorTexture is not null &&
            GetOrCreateAPIRenderObject(colorTexture) is IVkImageDescriptorSource source)
        {
            snapshotReady = source.TryGetDescriptorSnapshot(
                source.DescriptorViewType,
                requestedAspectMask: null,
                "window presentation source publication",
                allowSynchronousUpload: true,
                out snapshot);
        }

        uint sourceWidth = resolvedFrameBuffer?.Width ?? 0;
        uint sourceHeight = resolvedFrameBuffer?.Height ?? 0;
        if (snapshotReady &&
            _resourceRuntime.TryGetImageAllocationExtent(
                snapshot.Image.Handle,
                out Extent3D allocationExtent))
        {
            sourceWidth = allocationExtent.Width;
            sourceHeight = allocationExtent.Height;
        }

        return new VulkanPresentationSourceTuple(
            0, colorTexture, resolvedFrameBuffer, presentationPublisher,
            presentationPublicationToken, context,
            snapshotReady ? snapshot.Generation : 0,
            snapshotReady ? snapshot.Image : default,
            snapshotReady ? _commandRuntime.GetResourceGeneration(ObjectType.Image, snapshot.Image.Handle) : 0,
            snapshotReady ? snapshot.View : default,
            snapshotReady ? _commandRuntime.GetResourceGeneration(ObjectType.ImageView, snapshot.View.Handle) : 0,
            snapshotReady ? snapshot.Sampler : default,
            snapshotReady ? _commandRuntime.GetResourceGeneration(ObjectType.Sampler, snapshot.Sampler.Handle) : 0,
            snapshotReady ? snapshot.Format : default,
            snapshotReady ? snapshot.Aspect : default,
            snapshotReady ? snapshot.Samples : default,
            snapshotReady ? snapshot.TrackedLayout : ImageLayout.Undefined,
            sourceWidth, sourceHeight,
            default, 0, -1, 0, default, 0);
    }

    private void PublishPresentationSourceCompatibilityFields(
        in VulkanPresentationSourceTuple published)
    {
        // Readback and preview consumers are deliberately outside command
        // selection, but they still need the same retained logical source.
        _outputRuntime.PresentationSource.ColorTexture = published.ColorTexture;
        _outputRuntime.PresentationSource.FrameBuffer = published.FrameBuffer;
        _outputRuntime.PresentationSource.FrameOpContext = published.Context;
    }

    /// <summary>
    /// Gives descriptor authority to the last accepted desktop presentation draw.
    /// Eager compatibility publication can observe commands that the sealed plan
    /// rejects, so it must never decide the source used for native presentation.
    /// </summary>
    private void SelectWindowPresentationSourceFromAcceptedOperations(
        FrameOperationSequence operations)
    {
        for (int index = operations.Length - 1; index >= 0; index--)
        {
            if (operations.GetTarget(index) is not null ||
                !operations.TryGetMeshDraw(index, out MeshDrawPayload payload))
            {
                continue;
            }

            PendingMeshDraw draw = payload.Draw;
            WindowPresentationSourceMarker marker = draw.WindowPresentationSourceMarker;
            XRTexture? samplerTexture = null;
            bool hasFrozenSourceTexture = draw.ProgramBindingSnapshot?.TryGetSamplerTexture(
                "SourceTexture",
                out samplerTexture) == true;
            bool candidateSelected = marker.HasSource && marker.HasDeferredAuthority &&
                hasFrozenSourceTexture &&
                ReferenceEquals(marker.SourceTexture, samplerTexture);
            int passIndex = operations.GetHeader(index).PassIndex;
            if (VulkanMeshRenderingConventions.DescriptorTraceEnabled &&
                (passIndex == 100072 || marker.HasSource || hasFrozenSourceTexture))
            {
                FrameOpContext traceContext = operations.GetContext(index);
                string selectionReason = candidateSelected
                    ? "selected"
                    : !marker.HasSource
                        ? "no-marker"
                        : !marker.HasDeferredAuthority
                            ? "deferred-authority-missing"
                        : !hasFrozenSourceTexture
                            ? "frozen-source-missing"
                            : "texture-mismatch";
                Debug.VulkanEvery(
                    $"Vulkan.FinalPresentation.Select.{index}.{traceContext.PipelineIdentity}.{traceContext.ViewportIdentity}.{traceContext.OutputTargetIdentity}.{marker.HasSource}.{candidateSelected}",
                    TimeSpan.FromSeconds(1),
                    "[VulkanDescriptor] final-source candidate op={0}/{1} pass={2} pipeline={3} viewport={4} output={5} marker={6} markerTexture={7} markerFbo={8} frozenSource={9} frozenTexture={10} selected={11} reason={12}.",
                    index, operations.Length, passIndex,
                    traceContext.PipelineIdentity, traceContext.ViewportIdentity,
                    traceContext.OutputTargetIdentity, marker.HasSource,
                    marker.SourceTexture is null ? 0 : RuntimeHelpers.GetHashCode(marker.SourceTexture),
                    marker.SourceFrameBuffer is null ? 0 : RuntimeHelpers.GetHashCode(marker.SourceFrameBuffer),
                    hasFrozenSourceTexture,
                    samplerTexture is null ? 0 : RuntimeHelpers.GetHashCode(samplerTexture),
                    candidateSelected, selectionReason);
            }
            if (!candidateSelected)
            {
                continue;
            }

            FrameOpContext context = operations.GetContext(index);
            VulkanPresentationSourceTuple candidate =
                CreateWindowPresentationSourceTuple(
                    marker.SourceTexture,
                    marker.SourceFrameBuffer,
                    in context,
                    resolveNativeSource: false,
                    presentationPublisher: marker.Publisher,
                    presentationPublicationToken: marker.PublicationToken);
            VulkanPresentationSourceTuple selected = _windowPresentSource.SelectLogical(
                in candidate);
            PublishPresentationSourceCompatibilityFields(in selected);
            return;
        }
    }

    internal RenderTextureSamplingState GetTextureShaderSamplingState(XRTexture? texture)
    {
        if (texture is null || GetOrCreateAPIRenderObject(texture) is not IVkImageDescriptorSource source)
            return default;

        bool descriptorReady = source.TryGetDescriptorSnapshot(null, null, "shader sampling readiness", false, out VkImageDescriptorSnapshot snapshot);
        bool isReady = descriptorReady && snapshot.View.Handle != 0 &&
            _resourceRuntime.Images.IsLiveBackedByLiveImage(snapshot.View) &&
            (snapshot.Usage & ImageUsageFlags.SampledBit) != 0;
        ulong descriptorGeneration = descriptorReady ? snapshot.Generation : source.DescriptorGeneration;
        if (DescriptorTraceEnabled)
        {
            Debug.VulkanEvery(
                $"Vulkan.Descriptor.SamplingState.{texture.GetHashCode()}.{snapshot.Generation}.{snapshot.Image.Handle}.{snapshot.View.Handle}.{snapshot.Sampler.Handle}.{isReady}",
                TimeSpan.FromSeconds(2),
                "[VulkanDescriptor] sampling-state texture='{0}' ready={1} descriptorReady={2} generation={3} image=0x{4:X} view=0x{5:X} sampler=0x{6:X} usage={7}.",
                texture.Name ?? texture.GetDescribingName(), isReady, descriptorReady, descriptorGeneration,
                snapshot.Image.Handle, snapshot.View.Handle, snapshot.Sampler.Handle, snapshot.Usage);
        }
        return RenderTextureSamplingState.FromBackendGeneration(isReady, descriptorGeneration);
    }

    internal void BindFrameBuffer(EFramebufferTarget target, XRFrameBuffer? frameBuffer)
    {
        switch (target)
        {
            case EFramebufferTarget.Framebuffer:
                _commandRuntime.ActiveBoundReadFrameBuffer = frameBuffer;
                _commandRuntime.ActiveBoundDrawFrameBuffer = frameBuffer;
                break;
            case EFramebufferTarget.ReadFramebuffer:
                _commandRuntime.ActiveBoundReadFrameBuffer = frameBuffer;
                break;
            case EFramebufferTarget.DrawFramebuffer:
                _commandRuntime.ActiveBoundDrawFrameBuffer = frameBuffer;
                break;
            default:
                return;
        }

        XRFrameBuffer? drawFrameBuffer = _commandRuntime.ActiveBoundDrawFrameBuffer;
        if (drawFrameBuffer is null)
            _commandRuntime.ActiveState.SetCurrentTargetExtent(
                ResolveUnboundDrawTargetExtent());
        else
            _commandRuntime.ActiveState.SetCurrentTargetExtent(new Extent2D(Math.Max(drawFrameBuffer.Width, 1u), Math.Max(drawFrameBuffer.Height, 1u)));

        if (frameBuffer is not null)
            (GetOrCreateAPIRenderObject(frameBuffer, generateNow: true) as VkFrameBuffer)?.Generate();
    }

    /// <summary>
    /// Resolves the extent used by the default viewport and scissor while no engine framebuffer is bound.
    /// Presentationless explicit frames have no desktop swapchain, so their scoped output must win over the
    /// desktop placeholder extent. OpenXR remains the higher-priority externally owned target authority.
    /// </summary>
    private Extent2D ResolveUnboundDrawTargetExtent()
    {
        if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))
            return externalExtent;

        RenderFrameOutputDescription? output = AbstractRenderer.Current?.CurrentFrameOutput;
        if (output is { IsValid: true } explicitOutput &&
            explicitOutput.ExecutionMode is RenderExecutionMode.Presentationless or RenderExecutionMode.Component)
        {
            return new Extent2D(explicitOutput.Properties.Width, explicitOutput.Properties.Height);
        }

        return OutputRuntime.Desktop.Extent;
    }

    internal void Clear(bool color, bool depth, bool stencil)
    {
        if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline is null)
            return;

        _commandRuntime.ActiveState.SetClearState(color, depth, stencil);
        FrameOpContext context = CaptureFrameOpContextForCurrentPipelineScope();
        Extent2D extent = _commandRuntime.ResolveCurrentDrawTargetExtent();
        Rect2D rect = _commandRuntime.ActiveState.GetCroppingEnabled()
            ? _commandRuntime.ActiveState.GetScissor(extent)
            : new Rect2D(new Offset2D(0, 0), extent);
        EnqueueFrameOp(ClearOp.Rent(
            VulkanCommandRuntime.EnsureValidPassIndex(RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, "Clear", context.PassMetadata),
            _commandRuntime.ResolveCurrentFrameOpDrawTarget(), color, depth, stencil,
            _commandRuntime.ActiveState.GetClearColorValue(), _commandRuntime.ActiveState.GetClearDepthValue(),
            _commandRuntime.ActiveState.GetClearStencilValue(), rect, context));
    }

    internal byte GetStencilIndex(float x, float y)
    {
        XRFrameBuffer? frameBuffer = _commandRuntime.ActiveBoundReadFrameBuffer ?? _commandRuntime.GetCurrentDrawFrameBuffer();
        if (frameBuffer is not null)
        {
            int sampleX = Math.Clamp((int)x, 0, Math.Max((int)frameBuffer.Width - 1, 0));
            int sampleY = Math.Clamp((int)y, 0, Math.Max((int)frameBuffer.Height - 1, 0));
            if (TryResolveBlitImage(frameBuffer, OutputRuntime.Desktop.LastPresentedImageIndex, _commandRuntime.ActiveReadBufferMode, false, false, true, out BlitImageInfo source, true) &&
                _commandRuntime.TryReadStencilPixel(source, sampleX, sampleY, out byte value))
                return value;
        }

        if (_outputRuntime.DesktopDepthImage.Handle == 0)
            return 0;
        int xClamped = Math.Clamp((int)x, 0, Math.Max((int)OutputRuntime.Desktop.Extent.Width - 1, 0));
        int yClamped = Math.Clamp((int)y, 0, Math.Max((int)OutputRuntime.Desktop.Extent.Height - 1, 0));
        BlitImageInfo swapchainSource = ResolveSwapchainBlitImage(OutputRuntime.Desktop.LastPresentedImageIndex, false, false, true);
        return swapchainSource.IsValid && _commandRuntime.TryReadStencilPixel(swapchainSource, xClamped, yClamped, out byte swapchainValue)
            ? swapchainValue : (byte)0;
    }

    private long _deviceWaitIdleCalls;

    /// <summary>Counts actual device-wide waits, including explicit shutdown waits.</summary>
    internal long DeviceWaitIdleCalls => Interlocked.Read(ref _deviceWaitIdleCalls);

    internal void WaitForDeviceIdle()
    {
        ReaderWriterLockSlim admissionGate =
            _commandRuntime.CommandBuffers.DeviceQueueAdmissionGate;
        admissionGate.EnterWriteLock();
        try
        {
            if (!_deviceContext.IsOperational)
                return;
            Interlocked.Increment(ref _deviceWaitIdleCalls);
            Result result = _deviceContext.Api.DeviceWaitIdle(_deviceContext.Device);
            if (result == Result.Success)
            {
                _commandRuntime.CompleteTrackedDevice();
                return;
            }
            if (result == Result.ErrorDeviceLost)
            {
                MarkDeviceLost("DeviceWaitIdle returned ErrorDeviceLost", "vkDeviceWaitIdle", result);
                Debug.VulkanWarning("[Vulkan] DeviceWaitIdle returned ErrorDeviceLost. Device state is irrecoverable.");
                return;
            }

            throw new InvalidOperationException(
                $"vkDeviceWaitIdle failed with {result}.");
        }
        finally
        {
            admissionGate.ExitWriteLock();
        }
    }

    internal bool TryWaitForDeviceIdle(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The shutdown timeout must be non-negative.");
        if (IsDeviceLost)
            return false;
        if (_deviceContext.Device.Handle == 0)
            return true;
        Exception? failure = null;
        Thread waitThread = new(() => { try { WaitForDeviceIdle(); } catch (Exception ex) { failure = ex; } })
        { Name = "XRE-VulkanShutdownWait", IsBackground = true };
        waitThread.Start();
        TimeSpan maximumJoinTimeout = TimeSpan.FromMilliseconds(int.MaxValue);
        bool completed = waitThread.Join(timeout > maximumJoinTimeout ? maximumJoinTimeout : timeout);
        if (failure is not null)
            throw new InvalidOperationException("Vulkan device-idle wait failed during shutdown.", failure);
        return completed && !IsDeviceLost;
    }

    private XRFrameBuffer? ResolveWindowPresentFallbackFrameBuffer(XRTexture? colorTexture)
    {
        if (colorTexture is not IFrameBufferAttachement attachment)
            return null;
        if (!ReferenceEquals(_outputRuntime.PresentationSource.FallbackFrameBufferTexture, colorTexture))
        {
            _outputRuntime.PresentationSource.FallbackFrameBuffer = new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            { Name = $"{colorTexture.Name ?? "WindowPresentSource"}FBO" };
            _outputRuntime.PresentationSource.FallbackFrameBufferTexture = colorTexture;
        }
        return _outputRuntime.PresentationSource.FallbackFrameBuffer;
    }
}
