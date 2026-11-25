namespace XREngine.Editor;

/// <summary>
/// Serializes native dock rebuilds. A rebuild requested while one runs is coalesced into
/// one more pass after it. Each pass has a new revision, so callbacks created by an
/// earlier pass can detect that their controls no longer describe the dock.
/// </summary>
internal sealed class NativeDockRenderGate
{
    /// <summary>Passes that one rebuild request may run before the gate reports a loop.</summary>
    public const int MaximumPasses = 4;

    private bool _pending;

    /// <summary>Gets the revision of the most recent pass.</summary>
    public ulong Revision { get; private set; }

    /// <summary>Gets whether a pass is running.</summary>
    public bool IsRendering { get; private set; }

    /// <summary>Gets whether a callback from the pass with <paramref name="revision"/> may act.</summary>
    public bool IsCurrent(ulong revision) => !IsRendering && Revision == revision;

    /// <summary>
    /// Runs <paramref name="pass"/> until no nested request remains. A request made while a
    /// pass runs returns false and schedules one more pass. Throws when the passes do not settle.
    /// </summary>
    public bool Run(Action pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (IsRendering)
        {
            _pending = true;
            return false;
        }
        IsRendering = true;
        try
        {
            for (int count = 1; ; count++)
            {
                _pending = false;
                Revision++;
                pass();
                if (!_pending)
                    return true;
                if (count >= MaximumPasses)
                    throw new InvalidOperationException(
                        "Native dock rebuild callbacks kept requesting rebuilds.");
            }
        }
        catch
        {
            // Partial controls from a failed pass must not become active when the gate opens.
            Revision++;
            throw;
        }
        finally
        {
            _pending = false;
            IsRendering = false;
        }
    }
}
