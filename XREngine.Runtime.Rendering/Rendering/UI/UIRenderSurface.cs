using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI;

/// <summary>One leased color target whose reserved and completed generations are distinct.</summary>
internal sealed class UIRenderSurface : IUIDrawProducerLease
{
    private static readonly object QuarantineGate = new();
    private static readonly List<UIRenderSurface> Quarantined = [];
    private int _references = 1; // the pool's claim
    private long _reservedGeneration;
    private long _completedGeneration;

    internal UIRenderSurface(uint width, uint height, EPixelInternalFormat internalFormat,
        EPixelFormat format, EPixelType type)
    {
        Texture = XRTexture2D.CreateFrameBufferTexture(width, height, internalFormat,
            format, type, EFrameBufferAttachment.ColorAttachment0);
        FrameBuffer = new XRMaterialFrameBuffer(null, deriveRenderTargetsFromMaterial: false);
        try
        {
            FrameBuffer.SetRenderTargets((Texture, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        }
        catch
        {
            FrameBuffer.Destroy(true);
            Texture.Destroy(true);
            throw;
        }
    }

    public XRTexture2D Texture { get; }
    internal XRMaterialFrameBuffer FrameBuffer { get; }
    // This is the producer generation named by an immutable consumer payload.
    // Completion is a separate fact established only after the render receipt.
    public ulong Generation => ReservationGeneration;
    internal ulong CompletedGeneration => unchecked((ulong)Volatile.Read(ref _completedGeneration));
    internal ulong ReservationGeneration => unchecked((ulong)Volatile.Read(ref _reservedGeneration));
    internal bool IsPoolOnly => Volatile.Read(ref _references) == 1;
    internal uint Width => Texture.Width;
    internal uint Height => Texture.Height;

    internal void Resize(uint width, uint height)
    {
        if (!IsPoolOnly)
            throw new InvalidOperationException("A retained UI render surface cannot be resized.");
        if (Width != width || Height != height)
            FrameBuffer.Resize(width, height);
    }

    internal void Reserve(ulong generation)
    {
        if (!IsPoolOnly || generation == 0)
            throw new InvalidOperationException("The UI render surface is not free for a new generation.");
        Retain(); // writer claim; transfer another claim to the candidate before releasing it
        Volatile.Write(ref _reservedGeneration, checked((long)generation));
    }

    internal bool TryComplete(ulong generation)
    {
        if (generation == 0 || ReservationGeneration != generation ||
            Volatile.Read(ref _references) < 2)
            return false;
        Volatile.Write(ref _completedGeneration, checked((long)generation));
        return true;
    }

    public void Retain()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(current == 0, this);
        } while (Interlocked.CompareExchange(ref _references, checked(current + 1), current) != current);
    }

    public void Release()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _references);
            if (current == 0)
                throw new InvalidOperationException("A UI render surface claim was released twice.");
        } while (Interlocked.CompareExchange(ref _references, current - 1, current) != current);
        if (current != 1)
            return;
        try
        {
            if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                DestroyResources();
            else
                RuntimeEngine.EnqueueRenderThreadTask(DestroyResources,
                    "UI.RenderSurface.Destroy", RenderThreadJobKind.RenderPipelineResource);
        }
        catch
        {
            // Keep native wrappers rooted when retirement could not be scheduled.
            Quarantine();
            throw;
        }
    }

    private void DestroyResources()
    {
        // Destroy the attachment owner before its texture. A failed destroy keeps
        // this object rooted so the dependent texture cannot be silently freed.
        try
        {
            FrameBuffer.Destroy(true);
            Texture.Destroy(true);
        }
        catch
        {
            Quarantine();
            throw;
        }
    }

    private void Quarantine()
    {
        lock (QuarantineGate)
            if (!Quarantined.Contains(this))
                Quarantined.Add(this);
    }
}
