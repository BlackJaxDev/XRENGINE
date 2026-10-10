namespace XREngine.Rendering;

/// <summary>Retains deferred render resources until their final authoring use ends.</summary>
public abstract class RenderResourceLeaseOwner : IRenderResourceLeaseOwner
{
    private const int RetiringFlag = 1 << 30;
    private const int DisposedFlag = unchecked((int)0x80000000);
    private const int CountMask = RetiringFlag - 1;
    private int _lifetimeState;

    /// <summary>Gets the number of raw, authoring, and physical payload users.</summary>
    public int AuthoringUseCount => Volatile.Read(ref _lifetimeState) & CountMask;

    /// <summary>Gets whether this owner has stopped admitting new independent work.</summary>
    protected bool IsAuthoringRetired => (Volatile.Read(ref _lifetimeState) & RetiringFlag) != 0;

    /// <summary>Admits new work only while this owner is active.</summary>
    internal bool TryAcquireActiveUse()
    {
        while (true)
        {
            int state = Volatile.Read(ref _lifetimeState);
            if ((state & (RetiringFlag | DisposedFlag)) != 0)
                return false;
            if ((state & CountMask) == CountMask)
                throw new InvalidOperationException("Render resource authoring lease count overflowed.");
            if (Interlocked.CompareExchange(ref _lifetimeState, state + 1, state) == state)
                return true;
        }
    }

    /// <summary>Retains a use. A retiring owner permits transfers from a counted use.</summary>
    public void RetainAuthoringUse()
    {
        while (true)
        {
            int state = Volatile.Read(ref _lifetimeState);
            int count = state & CountMask;
            if ((state & RetiringFlag) != 0 && count == 0)
                throw new ObjectDisposedException(GetType().Name);
            if (count == CountMask)
                throw new InvalidOperationException("Render resource authoring lease count overflowed.");
            if (Interlocked.CompareExchange(ref _lifetimeState, state + 1, state) == state)
                return;
        }
    }

    /// <summary>Releases one use and disposes a retired owner after its final use.</summary>
    public void ReleaseAuthoringUse()
    {
        while (true)
        {
            int state = Volatile.Read(ref _lifetimeState);
            if ((state & CountMask) == 0)
                throw new InvalidOperationException("A render resource released an unowned authoring use.");
            int next = state - 1;
            bool dispose = (next & CountMask) == 0 && (next & RetiringFlag) != 0;
            if (dispose)
                next |= DisposedFlag;
            if (Interlocked.CompareExchange(ref _lifetimeState, next, state) != state)
                continue;
            if (dispose)
                DisposeRetainedResources();
            return;
        }
    }

    /// <summary>Stops new work and defers disposal until counted users finish.</summary>
    protected void RetireAuthoringResources()
    {
        while (true)
        {
            int state = Volatile.Read(ref _lifetimeState);
            if ((state & RetiringFlag) != 0)
                return;
            bool dispose = (state & CountMask) == 0;
            int next = state | RetiringFlag;
            if (dispose)
                next |= DisposedFlag;
            if (Interlocked.CompareExchange(ref _lifetimeState, next, state) != state)
                continue;
            if (dispose)
                DisposeRetainedResources();
            return;
        }
    }

    /// <summary>
    /// Disposes storage once, after retirement and the final counted use.
    /// Runs on the thread that retires an unused owner or releases its final use.
    /// </summary>
    protected abstract void DisposeRetainedResources();
}
