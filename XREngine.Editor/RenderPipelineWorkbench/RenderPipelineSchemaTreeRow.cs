namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>One cached row of the schema tree in the Settings view.</summary>
/// <param name="IsCategory">Whether the row is a category header.</param>
/// <param name="Label">The display text.</param>
/// <param name="StageKey">The stage key of a stage row, or an empty string for a category.</param>
/// <param name="Tooltip">The tooltip text, such as the category description.</param>
/// <param name="ConsumerCount">The number of commands that read the stage.</param>
public readonly record struct RenderPipelineSchemaTreeRow(
    bool IsCategory,
    string Label,
    string StageKey,
    string Tooltip,
    int ConsumerCount);
