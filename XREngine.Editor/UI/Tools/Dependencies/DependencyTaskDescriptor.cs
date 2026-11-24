namespace XREngine.Editor.Tools;

/// <summary>
/// Represents a runnable dependency script, native C++ bridge project, or managed build target
/// discovered from the workspace for the editor Dependencies and Builds panel.
/// </summary>
public sealed class DependencyTaskDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public required DependencyTaskKind Kind { get; init; }
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required string Synopsis { get; init; }
    public required string Description { get; init; }
    public required DependencyScriptParameterDescriptor[] Parameters { get; init; }
    public bool? IsReady { get; set; }
    public string StatusSummary { get; set; } = string.Empty;
}
