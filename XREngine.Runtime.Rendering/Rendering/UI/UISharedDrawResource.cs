using XREngine.Data.Core;

namespace XREngine.Rendering.UI;

/// <summary>Owns one immutable mesh or glyph buffer shared by captured draws.</summary>
internal sealed class UISharedDrawResource
{
    private static readonly List<UISharedDrawResource> Quarantined = [];
    private static readonly object Gate = new();
    private readonly long _bytes;
    private XRDataBuffer[] _buffers = [];
    private XRObjectBase? _owner;
    private int _references = 1;
    internal XRObjectBase Owner => _owner ?? throw new InvalidOperationException("UI geometry capture is incomplete.");

    private UISharedDrawResource(long bytes) => _bytes = bytes;

    internal static UISharedDrawResource CaptureMesh(XRMesh source, long bytes)
    {
        UISharedDrawResource resource = new(bytes);
        Reserve(bytes);
        try
        {
            resource._owner = source.Clone();
            resource._buffers = UIDrawPayload.CaptureOwnedBuffers(null, (XRMesh)resource._owner);
            foreach (XRDataBuffer buffer in resource._buffers)
                buffer.DisposeOnPush = false;
            return resource;
        }
        catch (Exception exception) { resource.CaptureFailed(exception); throw; }
    }

    internal static UISharedDrawResource CaptureBuffer(XRDataBuffer source, long bytes)
    {
        UISharedDrawResource resource = new(bytes);
        Reserve(bytes);
        try
        {
            XRDataBuffer buffer = source.Clone(true, source.Target);
            resource._owner = buffer;
            buffer.DisposeOnPush = false;
            return resource;
        }
        catch (Exception exception) { resource.CaptureFailed(exception); throw; }
    }

    private void CaptureFailed(Exception exception)
    {
        if (_owner is null)
            UIDrawPayloadBuilder.ReleaseBudget(_bytes, countPayload: false);
        else
            Quarantine(exception);
    }

    internal void RetirePartialCapture()
    {
        try
        {
            if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                ReleaseFromRenderOwner();
            else
                RuntimeEngine.EnqueueRenderThreadTask(() => ReleaseFromRenderOwner(),
                    "UI.DrawGeometry.ResourceRollback", RenderThreadJobKind.RenderPipelineResource);
        }
        catch (Exception exception) { Quarantine(exception); }
    }

    private void Quarantine(Exception exception)
    {
        lock (Gate)
            Quarantined.Add(this);
        Debug.LogWarning($"UI.DrawGeometry.RetirementFailed: resource and capture budget remain retained. {exception.Message}");
    }

    private static void Reserve(long bytes)
    {
        if (!UIDrawPayloadBuilder.TryReserveBudget(bytes, countPayload: false))
            throw new NotSupportedException("Retained UI geometry exceeds the shared draw capture budget.");
    }

    internal void Retain()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(current == 0, this);
        } while (Interlocked.CompareExchange(ref _references, checked(current + 1), current) != current);
    }

    /// <summary>Releases on the render owner after its referencing renderer has retired.</summary>
    internal bool ReleaseFromRenderOwner()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _references);
            if (current == 0)
                throw new InvalidOperationException("Shared UI geometry claim was released twice.");
        } while (Interlocked.CompareExchange(ref _references, current - 1, current) != current);
        if (current != 1)
            return true;
        try
        {
            UIDrawPayload.DestroyOwner(Owner);
            if (Owner is XRDataBuffer buffer)
                buffer.Dispose();
            UIDrawPayload.VerifyBufferRetirement(_buffers);
            UIDrawPayloadBuilder.ReleaseBudget(_bytes, countPayload: false);
            return true;
        }
        catch (Exception exception)
        {
            Quarantine(exception);
            return false;
        }
    }
}
