namespace XREngine.Rendering;

/// <summary>The severity of a problem in a render pipeline script.</summary>
public enum ERenderPipelineScriptProblemSeverity
{
    /// <summary>The script or the item is not correct. An item with an error is not loaded.</summary>
    Error,

    /// <summary>The script loads, but an item needs attention.</summary>
    Warning,
}
