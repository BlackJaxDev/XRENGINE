namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    private string? _lastRenderDeclineReason;

    /// <summary>
    /// The reason the latest render attempt declined command execution, or null
    /// when the attempt has not declined. Retained independently of log verbosity.
    /// </summary>
    public string? LastRenderDeclineReason => _lastRenderDeclineReason;

    private bool DeclineRender(string reason)
    {
        SetField(ref _lastRenderDeclineReason, reason, nameof(LastRenderDeclineReason));
        return false;
    }
}
