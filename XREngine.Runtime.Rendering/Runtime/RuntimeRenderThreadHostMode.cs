namespace XREngine;

/// <summary>Selects native event and render loop ownership.</summary>
public enum RuntimeRenderThreadHostMode
{
    CollapsedWindowRenderThread,
    SplitWindowPumpPrototype,
}
