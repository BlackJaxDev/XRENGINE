using System.Threading;

namespace XREngine.Rendering;

/// <summary>
/// Identifies one active capture owner across replacement output framebuffers.
/// </summary>
public sealed class RenderCaptureOwnerToken
{
    private static long s_nextId;
    private static long s_closedRevision;
    private int _open = 1;

    public RenderCaptureOwnerToken()
    {
        Id = unchecked((ulong)Interlocked.Increment(ref s_nextId));
        if (Id == 0)
            Id = unchecked((ulong)Interlocked.Increment(ref s_nextId));
    }

    public ulong Id { get; }
    public bool IsOpen => Volatile.Read(ref _open) != 0;
    public static long ClosedRevision => Volatile.Read(ref s_closedRevision);

    /// <summary>Stops new work for this capture owner.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _open, 0) == 1)
            Interlocked.Increment(ref s_closedRevision);
    }
}
