using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// One cached row of the Workbench outline. All text is formatted when the row is built.
/// </summary>
public sealed class RenderPipelineOutlineRow
{
    /// <summary>Gets the index of this row in the outline.</summary>
    public required int RowIndex { get; init; }

    /// <summary>Gets the stable node id of the command or container.</summary>
    public required int NodeId { get; init; }

    /// <summary>Gets the row kind.</summary>
    public required ERenderPipelineOutlineRowKind Kind { get; init; }

    /// <summary>Gets the nesting depth.</summary>
    public required int Depth { get; init; }

    /// <summary>Gets the command of a command row, or null.</summary>
    public ViewportRenderCommand? Command { get; init; }

    /// <summary>Gets the container that holds the command, or the container of a header row.</summary>
    public required ViewportRenderCommandContainer Container { get; init; }

    /// <summary>Gets the index of the command in its container, or -1 for a header row.</summary>
    public int IndexInContainer { get; init; } = -1;

    /// <summary>Gets the index of the command in the structure snapshot, or -1.</summary>
    public int StructureIndex { get; init; } = -1;

    /// <summary>Gets the command category. Header rows use <see cref="ERenderCommandCategory.Flow"/>.</summary>
    public ERenderCommandCategory Category { get; init; }

    /// <summary>Gets the row text.</summary>
    public required string Label { get; init; }

    /// <summary>Gets the pass-count badge text, or an empty string when the command has no pass.</summary>
    public string PassBadge { get; init; } = string.Empty;

    /// <summary>Gets the number of passes that the command described.</summary>
    public int PassCount { get; init; }

    /// <summary>Gets whether the command does GPU work and described no pass.</summary>
    public bool HasGpuWorkWithoutPass { get; init; }

    /// <summary>Gets whether the command type has no <see cref="RenderCommandInfoAttribute"/>.</summary>
    public bool IsUncategorized { get; init; }

    /// <summary>Gets the lowercase text that search matches: type, label, pass names, and resource names.</summary>
    public required string SearchText { get; init; }

    /// <summary>Gets the row index one past the last row nested below this row.</summary>
    public int SubtreeEnd { get; set; }

    /// <summary>Gets whether rows are nested below this row.</summary>
    public bool HasChildren => SubtreeEnd > RowIndex + 1;

    /// <summary>Gets whether the row has a problem that the outline marks.</summary>
    public bool HasProblem => HasGpuWorkWithoutPass || IsUncategorized;
}
