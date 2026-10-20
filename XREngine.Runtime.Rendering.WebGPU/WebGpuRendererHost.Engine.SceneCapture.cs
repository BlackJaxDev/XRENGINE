using XREngine.Data.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost : IAsyncSceneCaptureBackendCapability
{
    private sealed class PendingSceneCapture(SceneCaptureRequest request, int session, long generation,
        CancellationToken cancellationToken)
    {
        internal readonly SceneCaptureRequest Request = request;
        internal readonly int Session = session;
        internal readonly long Generation = generation;
        internal readonly CancellationToken CancellationToken = cancellationToken;
        internal readonly TaskCompletionSource<SceneCaptureReadback> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Cancellation;
        internal WebGpuTexture2DArray? Texture;
        internal int Handle;
        internal uint RecordedFrame;
        internal bool Reading;
        internal WebGpuFrameBuffer? FrameBuffer;
        internal ulong AttachmentRevision;
        internal ulong LayerProductionTicket;
        internal Exception? Failure;
        internal WebGpuSceneCaptureOriginPlan? OriginPlan;
    }

    private readonly Queue<PendingSceneCapture> _sceneCaptures = new(4);
    private readonly HashSet<PendingSceneCapture> _outstandingSceneCaptures = [];
    private PendingSceneCapture? _recordedSceneCapture;
    private PendingSceneCapture? _failedSceneCapture;
    private IRuntimeRenderWorld? _activeSceneCaptureWorld;
    private SceneCaptureLightingSnapshot? _activeSceneCaptureLighting;
    private ulong _sceneCaptureLightingOutputGeneration;

    public Task<SceneCaptureReadback> CaptureSceneLayerAsync(SceneCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(Current, this))
            throw new InvalidOperationException("WebGPU.SceneCapture.OwnerRequired: enqueue capture in the renderer owner scope.");
        if (request.Viewport.PipelineRequest.Purpose != ERenderPipelinePurpose.OffscreenCapture ||
            request.Viewport.RenderPipeline is null || request.Color.IsDestroyed || request.Target.IsDestroyed ||
            request.ArrayLayer < 0 || request.ArrayLayer >= request.Color.Textures.Length)
            throw new ArgumentException("WebGPU.SceneCapture.InvalidOutput: an owned offscreen viewport and live array layer are required.", nameof(request));
        if (!request.IsCurrent())
            throw new OperationCanceledException("WebGPU.SceneCapture.ObsoleteSource: the capture source changed before recording.");
        if (_outstandingSceneCaptures.Count >= 4)
            throw new InvalidOperationException("WebGPU.SceneCapture.Capacity: four scene layer requests are already outstanding.");
        if (_advancedPipelineArtifacts is { } artifacts)
            request.Viewport.RenderPipeline.BindWebPipelineArtifacts(artifacts);
        request.Camera.PostProcessStates.TryGetState(request.Viewport.RenderPipeline.ID, out var authored);
        RenderPipelineResourceProfile profile = new(request.Color.Width, request.Color.Height,
            request.Color.Width, request.Color.Height, true, EAntiAliasingMode.None, 1, false,
            ExternalTargetKind: RenderPipelineExternalTargetKind.CallerProvidedFrameBuffer,
            OutputColorFormat: EPixelInternalFormat.Rgba16f);
        request.Viewport.RenderPipeline.PrepareForWebOutput(profile, authored, _shaderArtifacts);
        WebGpuPipelineAdmission.Validate(request.Viewport.RenderPipeline.CreateRequirements(BackendId, profile, authored),
            request.Viewport.RenderPipeline, _shaderArtifacts, this);
        PendingSceneCapture pending = new(request, _session, BackendGeneration, cancellationToken);
        _outstandingSceneCaptures.Add(pending);
        _sceneCaptures.Enqueue(pending);
        pending.Cancellation = cancellationToken.Register(() =>
        {
            // During recording, completion belongs to frame unwind. After submission,
            // the readback finally block owns the retained physical source and ticket.
            if (!_engineRecording && !pending.Reading)
            {
                RemoveQueuedSceneCapture(pending);
                FinishSceneCapture(pending, null, new OperationCanceledException(cancellationToken), cancellationCallback: true);
            }
        });
        if (pending.Completion.Task.IsCompleted) pending.Cancellation.Dispose();
        return pending.Completion.Task;
    }

    private bool RecordPendingSceneCapture()
    {
        SetField(ref _recordedSceneCapture, null, publishNotifications: false);
        while (_sceneCaptures.TryPeek(out PendingSceneCapture? pending))
        {
            if (pending.Completion.Task.IsCompleted)
            {
                _sceneCaptures.Dequeue();
                ReleaseSceneCapture(pending);
                continue;
            }
            try
            {
                RequireCurrentSceneCapture(pending);
                SceneCaptureRequest request = pending.Request;
                if (request.IsPrepared?.Invoke() == false)
                    return true;
                WebGpuTexture2DArray texture = (WebGpuTexture2DArray)GetOrCreateAPIRenderObject(request.Color, generateNow: true)!;
                WebGpuFrameBuffer framebuffer = (WebGpuFrameBuffer)GetOrCreateAPIRenderObject(request.Target, generateNow: true)!;
                framebuffer.EnsureCurrent();
                ulong attachmentRevision = framebuffer.Revision;
                bool exactLayer = false;
                foreach (var attachment in request.Target.Targets ?? [])
                    if (attachment.Attachment == EFrameBufferAttachment.ColorAttachment0 && ReferenceEquals(attachment.Target, request.Color) &&
                        attachment.MipLevel == 0 && attachment.LayerIndex == request.ArrayLayer)
                        exactLayer = true;
                if (!exactLayer)
                    throw new InvalidOperationException("WebGPU.SceneCapture.LayerMismatch: the output must attach the exact requested array layer.");
                if (texture.Format != "rgba16float" || texture.SampleCount != 1)
                    throw new NotSupportedException("WebGPU.SceneCapture.ColorFormat: scene layer readback requires single-sample RGBA16F.");
                if (request.NormalizeAtlasOrigin)
                {
                    pending.OriginPlan ??= new(this, texture.ResourceHandle, request.ArrayLayer, texture.Width, texture.Height);
                    if (!pending.OriginPlan.Matches(texture.ResourceHandle, request.ArrayLayer, texture.Width, texture.Height))
                        throw new InvalidOperationException("WebGPU.SceneCapture.OriginSourceChanged: normalization no longer owns its exact source layer.");
                    pending.OriginPlan.Prepare();
                }
                RenderTargetOutputProperties properties = new(texture.Width, texture.Height,
                    ColorFormat: EPixelInternalFormat.Rgba16f, DepthFormat: EPixelInternalFormat.Depth24Stencil8,
                    SampleCount: 1) { ColorEncoding = texture.Format, DepthEncoding = "depth24plus-stencil8" };
                RenderFrameOutputDescription output = new(RenderExecutionMode.Presentationless, properties,
                    checked((ulong)texture.ResourceHandle), 0);
                SetField(ref _activeSceneCaptureWorld, request.World, publishNotifications: false);
                SetField(ref _activeSceneCaptureLighting, request.Lighting, publishNotifications: false);
                SetField(ref _sceneCaptureLightingOutputGeneration, ReferenceEquals(request.World.Lights, _engineViewport?.World?.Lights)
                    ? CurrentFrameOutput?.TargetGeneration ?? 0 : 0, publishNotifications: false);
                try
                {
                    using FrameOutputScope outputScope = PushFrameOutput(output);
                    using var sceneScope = RenderWorldSnapshotPublication.EnterIsolatedScene(request.World.VisualScene);
                    using var timeScope = RenderFrameViewSetCapture.PushElapsedTime(request.FrozenElapsedTime);
                    using var captureScope = VPRCRenderTargetHelpers.PushSceneCapturePass();
                    if (!request.Viewport.TryRender(request.Target, request.World, request.Camera))
                        return false;
                }
                finally
                {
                    SetField(ref _activeSceneCaptureWorld, null, publishNotifications: false);
                    SetField(ref _activeSceneCaptureLighting, null, publishNotifications: false);
                    SetField(ref _sceneCaptureLightingOutputGeneration, 0UL, publishNotifications: false);
                }
                if (_engineDrawPending)
                    return false;
                if (!texture.WasProducedInFrame(_engineFrameSequence) ||
                    !framebuffer.WasColorProducedInFrame(_engineFrameSequence, attachmentRevision, request.Color, 0, request.ArrayLayer))
                    throw new InvalidOperationException("WebGPU.SceneCapture.OutputNotWritten: the authored pipeline did not produce its requested color layer.");
                if (pending.OriginPlan is { } origin)
                {
                    origin.Record();
                    texture.MarkSubresourceProduced(0, request.ArrayLayer);
                }
                pending.Texture = texture;
                pending.Handle = texture.ResourceHandle;
                pending.RecordedFrame = _engineFrameSequence;
                pending.LayerProductionTicket = texture.GetBaseLayerTicket(request.ArrayLayer);
                pending.FrameBuffer = framebuffer;
                pending.AttachmentRevision = framebuffer.Revision;
                SetField(ref _recordedSceneCapture, pending, publishNotifications: false);
                return true;
            }
            catch (RenderResourcePreparationPendingException) { return false; }
            catch (Exception error)
            {
                pending.Failure = error;
                SetField(ref _failedSceneCapture, pending, publishNotifications: false);
                _sceneCaptures.Dequeue();
                // Discard any commands already recorded by the failed capture.
                MarkEngineDrawPending();
                return false;
            }
        }
        return RecordPendingSceneCaptureFinalization();
    }

    private void CompleteAcceptedSceneCapture(bool accepted)
    {
        CompleteAcceptedSceneCaptureFinalization(accepted);
        CompleteAcceptedSceneLighting(accepted);
        if (_failedSceneCapture is { } failed)
        {
            SetField(ref _failedSceneCapture, null, publishNotifications: false);
            FinishSceneCapture(failed, null, failed.Failure!);
        }
        PendingSceneCapture? pending = _recordedSceneCapture;
        SetField(ref _recordedSceneCapture, null, publishNotifications: false);
        if (!accepted || pending is null || pending.RecordedFrame != _engineFrameSequence)
        {
            int count = _sceneCaptures.Count;
            for (int index = 0; index < count; index++)
            {
                PendingSceneCapture queuedCapture = _sceneCaptures.Dequeue();
                if (queuedCapture.Failure is { } failure) FinishSceneCapture(queuedCapture, null, failure);
                else if (queuedCapture.CancellationToken.IsCancellationRequested)
                    FinishSceneCapture(queuedCapture, null, new OperationCanceledException(queuedCapture.CancellationToken));
                else if (queuedCapture.Completion.Task.IsCompleted) ReleaseSceneCapture(queuedCapture);
                else _sceneCaptures.Enqueue(queuedCapture);
            }
            return;
        }
        if (!_sceneCaptures.TryDequeue(out PendingSceneCapture? queued) || !ReferenceEquals(queued, pending))
            throw new InvalidOperationException("WebGPU.SceneCapture.OwnerChanged: the accepted capture no longer owns its queue entry.");
        pending.Reading = true;
        _ = ReadAcceptedSceneCaptureAsync(pending);
    }

    private async Task ReadAcceptedSceneCaptureAsync(PendingSceneCapture pending)
    {
        SceneCaptureReadback? result = null;
        Exception? failure = null;
        try
        {
            RequireCurrentSceneCapture(pending);
            WebGpuTexture2DArray texture = pending.Texture!;
            if (!texture.IsCurrentGpuAllocationForCopy || texture.ResourceHandle != pending.Handle || !texture.HasCommittedProduction ||
                pending.FrameBuffer?.Revision != pending.AttachmentRevision ||
                !texture.IsCurrentCommittedBaseLayerTicket(pending.Request.ArrayLayer, pending.LayerProductionTicket))
                throw new InvalidOperationException("WebGPU.SceneCapture.ProducerObsolete: the accepted texture generation is no longer current.");
            SceneCaptureRequest request = pending.Request;
            int width = checked((int)texture.Width), height = checked((int)texture.Height);
            byte[] bytes = await ReadTextureAsync(new BrowserTextureReadbackDescription(pending.Handle, 0,
                0, 0, width, height, request.ArrayLayer, "rgba16float", pending.RecordedFrame), pending.CancellationToken);
            RequireCurrentSceneCapture(pending);
            if (!texture.IsCurrentGpuAllocationForCopy || texture.ResourceHandle != pending.Handle ||
                !texture.IsCurrentCommittedBaseLayerTicket(request.ArrayLayer, pending.LayerProductionTicket))
                throw new InvalidOperationException("WebGPU.SceneCapture.ReadbackObsolete: the captured texture was replaced before publication.");
            float[] rgba = new float[checked(width * height * 4)];
            BrowserTextureReadbackPixels.DecodeRgba16Float(bytes, rgba);
            result = new(rgba, width, height, request.ArrayLayer,
                pending.Generation, pending.Handle, pending.RecordedFrame)
            {
                ProducerOwner = this, Source = request.Color, Session = pending.Session,
                LayerProductionTicket = pending.LayerProductionTicket,
                PersistedContentHash = SHA256.HashData(MemoryMarshal.AsBytes(rgba.AsSpan())),
            };
        }
        catch (Exception error) { failure = error; }
        FinishSceneCapture(pending, result, failure);
    }

    private void RequireCurrentSceneCapture(PendingSceneCapture pending)
    {
        pending.CancellationToken.ThrowIfCancellationRequested();
        if (pending.Failure is { } failure) throw failure;
        RequireReadbackSession(pending.Session);
        if (pending.Generation != BackendGeneration || !pending.Request.IsCurrent())
            throw new OperationCanceledException("WebGPU.SceneCapture.ObsoleteSource: capture lifetime, world or source generation changed.");
        if (pending.Request.Lighting is { } lighting) RequireCapturedLighting(lighting);
    }

    private void FinishSceneCapture(PendingSceneCapture pending, SceneCaptureReadback? result, Exception? failure,
        bool cancellationCallback = false)
    {
        try { ReleaseSceneCapture(pending, cancellationCallback); }
        catch (Exception cleanupError) { failure = failure is null ? cleanupError : new AggregateException(failure, cleanupError); }
        if (failure is OperationCanceledException) pending.Completion.TrySetCanceled(pending.CancellationToken);
        else if (failure is not null) pending.Completion.TrySetException(failure);
        else pending.Completion.TrySetResult(result!);
    }

    private void ReleaseSceneCapture(PendingSceneCapture pending, bool cancellationCallback = false)
    {
        try
        {
            if (cancellationCallback) pending.Cancellation.Unregister();
            else pending.Cancellation.Dispose();
        }
        finally
        {
            try { ReleaseSceneCaptureResources(pending); }
            finally { _outstandingSceneCaptures.Remove(pending); }
        }
    }

    private static void ReleaseSceneCaptureResources(PendingSceneCapture pending)
    {
        WebGpuSceneCaptureOriginPlan? origin = pending.OriginPlan;
        pending.OriginPlan = null;
        origin?.Dispose();
    }

    private void RemoveQueuedSceneCapture(PendingSceneCapture pending)
    {
        int count = _sceneCaptures.Count;
        for (int index = 0; index < count; index++)
        {
            PendingSceneCapture queued = _sceneCaptures.Dequeue();
            if (!ReferenceEquals(queued, pending)) _sceneCaptures.Enqueue(queued);
        }
    }

    private void CancelSceneCaptures()
    {
        CancelSceneCaptureFinalizations();
        CancelSceneLighting();
        foreach (PendingSceneCapture pending in _outstandingSceneCaptures.ToArray())
        {
            pending.Failure = new InvalidOperationException("WebGPU.SceneCapture.SessionEnded: the owning renderer session ended.");
            if (_engineRecording || pending.Reading) continue;
            FinishSceneCapture(pending, null, pending.Failure);
        }
        if (!_engineRecording)
        {
            _outstandingSceneCaptures.RemoveWhere(static pending => !pending.Reading);
            _sceneCaptures.Clear();
            SetField(ref _recordedSceneCapture, null, publishNotifications: false);
        }
    }
}
