namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>One cached row of the Compare tab: a pass with its authored and backend positions.</summary>
/// <param name="PassIndex">The pass index.</param>
/// <param name="Name">The pass name.</param>
/// <param name="AuthoredText">The authored position as text.</param>
/// <param name="BackendText">The backend position as text, or a dash when the pass did not run.</param>
/// <param name="DeltaText">The position change as text, or an empty string when the pass did not move.</param>
/// <param name="Moved">Whether the backend position differs from the authored position.</param>
/// <param name="Executed">Whether the pass is in the backend plan.</param>
public readonly record struct RenderPipelinePlanCompareRow(
    int PassIndex,
    string Name,
    string AuthoredText,
    string BackendText,
    string DeltaText,
    bool Moved,
    bool Executed);
