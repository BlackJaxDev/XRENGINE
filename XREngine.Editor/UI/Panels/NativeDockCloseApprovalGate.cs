using XREngine.Rendering;

namespace XREngine.Editor;

/// <summary>Accepts only the latest approved close for one detached window instance.</summary>
internal sealed class NativeDockCloseApprovalGate(object window)
{
    private long _latestSequence;

    internal bool Register(object candidate, WindowCloseApproval approval)
    {
        if (!ReferenceEquals(window, candidate) || approval.RequestSequence == 0)
            return false;
        Interlocked.Exchange(ref _latestSequence, (long)approval.RequestSequence);
        return true;
    }

    internal bool TryConsume(object candidate, WindowCloseApproval approval)
        => ReferenceEquals(window, candidate) && approval.RequestSequence != 0 &&
           Interlocked.CompareExchange(ref _latestSequence, 0,
               (long)approval.RequestSequence) == (long)approval.RequestSequence;
}
