namespace XREngine.Editor;

internal sealed class ToolbarIconRendererFrameState
{
    public ulong LastProcessedFrame { get; set; } = ulong.MaxValue;
    public bool ReadySummaryLogged { get; set; }
}
