namespace XREngine;

public enum ERenderOutputWorkDisposition
{
    FreshRender,
    ReusedCurrent,
    ReusedStale,
    Deferred,
    Skipped,
    QualityReduced,
}
