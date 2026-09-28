namespace XREngine.Rendering;

/// <summary>Readiness and terminal states of one asynchronously initialized browser renderer.</summary>
public enum BrowserRendererState
{
    Pending,
    Ready,
    Failed,
    Lost,
    Disposed
}
