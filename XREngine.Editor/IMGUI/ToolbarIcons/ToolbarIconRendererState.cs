namespace XREngine.Editor;

internal sealed class ToolbarIconRendererState
{
    public int AttemptCount { get; set; }
    public ulong NextRetryFrame { get; set; }
    public nint Handle { get; set; }
    public bool RequiresVerticalFlip { get; set; }
    public bool IsReady { get; set; }
    public bool FailureLogged { get; set; }
}
