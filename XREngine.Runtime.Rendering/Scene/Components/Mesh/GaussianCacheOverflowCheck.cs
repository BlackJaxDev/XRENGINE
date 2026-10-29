namespace XREngine.Components.Scene.Mesh;

/// <summary>
/// Decides when <see cref="GaussianCacheComponent"/> reads the overflow counter of its splat renderer.
/// </summary>
/// <remarks>
/// <para>
/// A read starts only when the loaded nodes are more than the renderer capacity, because only then can the cut choose
/// more splats than fit. The component then copies the counter of that frame into its own readback buffer on the GPU and
/// inserts a fence. It reads the readback buffer only after the fence signals.
/// </para>
/// <para>
/// One read ends the checks: a read without overflow, or a read that logs the overflow. <see cref="Rearm"/> starts them
/// again and allows a new log (a new capacity, a new <see cref="GaussianCacheComponent.MaxRenderedSplats"/>, or a new
/// cache). <see cref="RearmForNewNodes"/> starts them again after more nodes load, but only when no overflow is logged.
/// </para>
/// </remarks>
internal sealed class GaussianCacheOverflowCheck
{
    private bool _pending;
    private bool _finished;

    /// <summary>True while a read waits for its fence.</summary>
    public bool IsPending => _pending;

    /// <summary>True when no further read occurs until a rearm.</summary>
    public bool IsFinished => _finished;

    /// <summary>True after an overflow is logged. Only <see cref="Rearm"/> clears it.</summary>
    public bool HasLogged { get; private set; }

    /// <summary>Starts the checks again and allows a new log. A pending read is abandoned; the caller drops its fence.</summary>
    public void Rearm()
    {
        _pending = false;
        _finished = false;
        HasLogged = false;
    }

    /// <summary>Starts the checks again after more nodes load. It has no effect after an overflow is logged or while a read is pending.</summary>
    public void RearmForNewNodes()
    {
        if (!HasLogged && !_pending)
            _finished = false;
    }

    /// <summary>
    /// Returns true when the caller must start a read in this frame: no read is pending or finished, and the loaded nodes
    /// are more than the capacity.
    /// </summary>
    public bool ShouldStartRead(long loadedNodeCount, int capacity)
        => !_pending && !_finished && loadedNodeCount > capacity;

    /// <summary>The caller copied the counter and inserted a fence.</summary>
    public void ReadStarted()
        => _pending = true;

    /// <summary>The backend cannot copy the counter or insert a fence. No read occurs until a rearm.</summary>
    public void ReadUnavailable()
    {
        _pending = false;
        _finished = true;
    }

    /// <summary>The fence failed. A later frame starts a new read.</summary>
    public void ReadFailed()
        => _pending = false;

    /// <summary>Ends the checks with the value of the counter. Returns true when the caller must log the overflow.</summary>
    public bool ReadCompleted(uint droppedSplats)
    {
        _pending = false;
        _finished = true;
        if (droppedSplats == 0u || HasLogged)
            return false;

        HasLogged = true;
        return true;
    }
}
