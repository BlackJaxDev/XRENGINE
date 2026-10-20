using System.Security.Cryptography;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private sealed class PendingSceneFinalization(SceneCaptureFinalizeRequest request, WebGpuTexture2DArray texture,
        int handle, int session, long generation, CancellationToken cancellationToken)
    {
        internal readonly SceneCaptureFinalizeRequest Request = request;
        internal readonly WebGpuTexture2DArray Texture = texture;
        internal readonly int Handle = handle, Session = session;
        internal readonly long Generation = generation;
        internal readonly CancellationToken CancellationToken = cancellationToken;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Mipmap2D[] Mips = new Mipmap2D[26];
        internal readonly DataSource[] Pixels = new DataSource[26];
        internal readonly SceneCaptureReadback[] InputReceipts = new SceneCaptureReadback[26];
        internal readonly SceneCaptureReadback[] Receipts = new SceneCaptureReadback[26];
        internal CancellationTokenRegistration Cancellation;
        internal uint RecordedFrame;
        internal bool Submitted;
        internal Exception? Failure;
    }

    private PendingSceneFinalization? _sceneCaptureFinalization;

    public unsafe Task FinalizeSceneCaptureAsync(SceneCaptureFinalizeRequest request, CancellationToken cancellationToken = default)
    {
        RequireReady();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (!ReferenceEquals(Current, this) || _sceneCaptureFinalization is not null)
            throw new InvalidOperationException("WebGPU.SceneCapture.FinalizationOwner: one atlas finalization may be owned by the renderer at a time.");
        if (!request.IsCurrent() || request.Color.IsDestroyed || request.Layers.Count != 26 || request.Color.Textures.Length != 26 ||
            !TryGetAPIRenderObject(request.Color, out AbstractRenderAPIObject? wrapper) || wrapper is not WebGpuTexture2DArray texture)
            throw new InvalidOperationException("WebGPU.SceneCapture.FinalizationSource: the complete current captured array and 26 exact receipts are required.");
        PendingSceneFinalization pending = new(request, texture, texture.ResourceHandle, _session, BackendGeneration, cancellationToken);
        for (int layer = 0; layer < 26; layer++)
        {
            SceneCaptureReadback receipt = request.Layers[layer];
            XRTexture2D slice = request.Color.Textures[layer];
            if (!ReferenceEquals(receipt.ProducerOwner, this) || !ReferenceEquals(receipt.Source, request.Color) ||
                receipt.Session != _session || receipt.BackendGeneration != BackendGeneration || receipt.TextureGenerationHandle != pending.Handle ||
                receipt.ArrayLayer != layer || receipt.Width != request.Color.Width || receipt.Height != request.Color.Height ||
                receipt.AcceptedFrameSequence == 0 || slice.Mipmaps.Length != 1 ||
                !texture.IsCurrentCommittedBaseLayerTicket(layer, receipt.LayerProductionTicket))
                throw new InvalidOperationException("WebGPU.SceneCapture.LayerReceiptMismatch: every layer must belong to this exact accepted atlas generation.");
            Mipmap2D mip = slice.Mipmaps[0];
            DataSource data = mip.Data ?? throw new InvalidOperationException("WebGPU.SceneCapture.CpuLayerMissing");
            if (data.IsDisposed || data.Address == VoidPtr.Zero || data.Length != checked((uint)(receipt.Width * receipt.Height * 16)) ||
                mip.PixelFormat != EPixelFormat.Rgba || mip.PixelType != EPixelType.Float ||
                !CryptographicOperations.FixedTimeEquals(receipt.PersistedContentHash,
                    SHA256.HashData(new ReadOnlySpan<byte>((void*)data.Address, checked((int)data.Length)))))
                throw new InvalidOperationException("WebGPU.SceneCapture.CpuLayerMismatch: persisted floats must exactly match their completed GPU readback.");
            pending.Mips[layer] = mip;
            pending.Pixels[layer] = data;
            pending.InputReceipts[layer] = receipt;
            pending.Receipts[layer] = receipt.WithoutPixels(copyHash: true);
        }
        texture.AdoptCapturedBaseMipmaps(pending.Handle);
        RequireCurrentSceneFinalization(pending);
        if (texture.MipLevelCount == 1)
            return Task.CompletedTask;
        SetField(ref _sceneCaptureFinalization, pending, publishNotifications: false);
        pending.Cancellation = cancellationToken.Register(() =>
        {
            if (!_engineRecording && !pending.Submitted)
            {
                if (ReferenceEquals(_sceneCaptureFinalization, pending)) SetField(ref _sceneCaptureFinalization, null, publishNotifications: false);
                pending.Cancellation.Unregister();
                pending.Completion.TrySetCanceled(cancellationToken);
            }
        });
        if (pending.Completion.Task.IsCompleted) pending.Cancellation.Dispose();
        return pending.Completion.Task;
    }

    private bool RecordPendingSceneCaptureFinalization()
    {
        PendingSceneFinalization? pending = _sceneCaptureFinalization;
        if (pending is null || pending.Submitted || pending.Completion.Task.IsCompleted) return true;
        try
        {
            RequireCurrentSceneFinalization(pending);
            pending.Texture.PrepareCapturedMipmaps(pending.Handle);
            pending.Texture.RecordCapturedMipmaps(pending.Handle);
            pending.RecordedFrame = _engineFrameSequence;
            return true;
        }
        catch (RenderResourcePreparationPendingException) { return false; }
        catch (Exception error)
        {
            pending.Failure = error;
            MarkEngineDrawPending();
            return false;
        }
    }

    private void CompleteAcceptedSceneCaptureFinalization(bool accepted)
    {
        PendingSceneFinalization? pending = _sceneCaptureFinalization;
        if (pending is null || pending.Submitted) return;
        if (pending.Failure is { } failure)
        {
            pending.Completion.TrySetException(failure);
            ReleaseSceneFinalization(pending);
            return;
        }
        if (pending.CancellationToken.IsCancellationRequested)
        {
            pending.Completion.TrySetCanceled(pending.CancellationToken);
            ReleaseSceneFinalization(pending);
            return;
        }
        if (!accepted || pending.RecordedFrame != _engineFrameSequence) return;
        pending.Submitted = true;
        _ = CompleteSceneFinalizationAsync(pending);
    }

    private async Task CompleteSceneFinalizationAsync(PendingSceneFinalization pending)
    {
        try
        {
            await CompleteSubmittedWorkTicketAsync(pending.CancellationToken, pending.RecordedFrame);
            RequireCurrentSceneFinalization(pending);
            RequireFinalizedCpuContent(pending);
            if (!pending.Texture.HasCommittedProduction)
                throw new InvalidOperationException("WebGPU.SceneCapture.MipProducerUncommitted: the complete mip generation was not accepted.");
            pending.Completion.TrySetResult();
        }
        catch (OperationCanceledException) { pending.Completion.TrySetCanceled(); }
        catch (Exception error) { pending.Completion.TrySetException(error); }
        finally { ReleaseSceneFinalization(pending); }
    }

    private void RequireCurrentSceneFinalization(PendingSceneFinalization pending)
    {
        pending.CancellationToken.ThrowIfCancellationRequested();
        if (pending.Failure is { } failure) throw failure;
        RequireReadbackSession(pending.Session);
        if (pending.Generation != BackendGeneration || !pending.Request.IsCurrent() ||
            pending.Request.Color.IsDestroyed || pending.Texture.ResourceHandle != pending.Handle || !pending.Texture.IsCurrentGpuAllocationForCopy ||
            pending.Request.Color.Textures.Length != 26 || pending.Request.Layers.Count != 26)
            throw new OperationCanceledException("WebGPU.SceneCapture.FinalizationObsolete: the captured atlas no longer belongs to this request.");
        for (int layer = 0; layer < 26; layer++)
        {
            if (!ReferenceEquals(pending.Request.Layers[layer], pending.InputReceipts[layer]))
                throw new OperationCanceledException("WebGPU.SceneCapture.LayerReceiptReplaced: a caller-owned receipt changed during finalization.");
            if (!pending.Texture.IsCurrentCommittedBaseLayerTicket(layer, pending.Receipts[layer].LayerProductionTicket))
                throw new OperationCanceledException("WebGPU.SceneCapture.LayerContentChanged: an accepted base layer was overwritten during finalization.");
            XRTexture2D slice = pending.Request.Color.Textures[layer];
            if (slice.Mipmaps.Length != 1 || !ReferenceEquals(slice.Mipmaps[0], pending.Mips[layer]) ||
                !ReferenceEquals(slice.Mipmaps[0].Data, pending.Pixels[layer]) || pending.Pixels[layer].IsDisposed)
                throw new OperationCanceledException("WebGPU.SceneCapture.CpuLayerReplaced: a persisted layer changed during finalization.");
        }
    }

    private static unsafe void RequireFinalizedCpuContent(PendingSceneFinalization pending)
    {
        for (int layer = 0; layer < 26; layer++)
        {
            DataSource data = pending.Pixels[layer];
            if (data.IsDisposed || data.Address == VoidPtr.Zero || !CryptographicOperations.FixedTimeEquals(
                pending.Receipts[layer].PersistedContentHash,
                SHA256.HashData(new ReadOnlySpan<byte>((void*)data.Address, checked((int)data.Length)))))
                throw new InvalidOperationException("WebGPU.SceneCapture.CpuLayerChanged: persisted pixels changed while the GPU mip producer was pending.");
        }
    }

    private void ReleaseSceneFinalization(PendingSceneFinalization pending)
    {
        pending.Cancellation.Dispose();
        if (ReferenceEquals(_sceneCaptureFinalization, pending)) SetField(ref _sceneCaptureFinalization, null, publishNotifications: false);
    }

    private void CancelSceneCaptureFinalizations()
    {
        if (_sceneCaptureFinalization is not { } pending) return;
        pending.Failure = new InvalidOperationException("WebGPU.SceneCapture.FinalizationSessionEnded: the renderer session ended.");
        if (_engineRecording || pending.Submitted) return;
        pending.Completion.TrySetException(pending.Failure);
        ReleaseSceneFinalization(pending);
    }
}
