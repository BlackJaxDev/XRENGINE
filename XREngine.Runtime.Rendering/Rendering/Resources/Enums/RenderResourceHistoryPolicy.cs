namespace XREngine.Rendering.Resources;

public enum RenderResourceHistoryPolicy
{
    None,
    SeedFromCurrentFrame,
    ClearOnCommit,
    PreserveWhenCompatible,
}
