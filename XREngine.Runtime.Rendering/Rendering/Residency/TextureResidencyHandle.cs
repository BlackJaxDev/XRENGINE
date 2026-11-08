namespace XREngine.Rendering.Residency;

/// <summary>
/// An owned texture residency request from <see cref="ITextureResidencyRequests.RequestResidency"/>.
/// The request stays active until the owner disposes the handle.
/// </summary>
/// <remarks>
/// A handle that is never disposed keeps the mip resident until the process ends or the texture is destroyed. Streaming
/// does not keep the handle alive and the handle has no finalizer, so a lost handle cannot cancel its request.
/// Any thread can read the members and dispose the handle. Dispose is idempotent.
/// </remarks>
public sealed class TextureResidencyHandle : IDisposable
{
    private readonly ImportedTextureStreamingManager _owner;
    private int _disposed;

    internal TextureResidencyHandle(ImportedTextureStreamingManager owner, TextureResidencyTicket ticket)
    {
        _owner = owner;
        Ticket = ticket;
    }

    /// <summary>
    /// The ticket of the request. <see cref="ITextureResidencyRequests.ResidencyRequestCompleted"/> reports this ticket.
    /// </summary>
    public TextureResidencyTicket Ticket { get; }

    /// <summary>True after <see cref="Dispose"/>.</summary>
    public bool IsDisposed
        => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// The state of the request. It is <see cref="ETextureResidencyRequestState.None"/> after <see cref="Dispose"/>,
    /// and after streaming dropped the request because its texture was destroyed. It does not throw.
    /// </summary>
    public ETextureResidencyRequestState State
        => IsDisposed ? ETextureResidencyRequestState.None : _owner.GetResidencyRequestState(Ticket);

    /// <summary>
    /// Cancels the request. A resident request releases its hold, and the texture goes back to visibility-driven
    /// residency; the cancel does not force a demotion. A pending promotion that no other request needs is cancelled.
    /// It does not throw.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _owner.CancelResidency(Ticket);
    }
}
