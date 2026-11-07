using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// The cached display text of one Resources view row. The text is built once for each resource snapshot, so draw
/// code does not format strings each frame.
/// </summary>
public sealed class RenderPipelineWorkbenchResourceRow
{
    /// <summary>Gets the snapshot entry.</summary>
    public required RenderPipelineResourceEntry Entry { get; init; }

    /// <summary>Gets the kind text, or "undeclared".</summary>
    public required string KindText { get; init; }

    /// <summary>Gets the lifetime text.</summary>
    public required string LifetimeText { get; init; }

    /// <summary>Gets the size text, such as <c>1920x1080 x2</c>.</summary>
    public required string SizeText { get; init; }

    /// <summary>Gets the estimated memory text.</summary>
    public required string BytesText { get; init; }

    /// <summary>Gets the writer and reader counts, such as <c>2 / 5</c>.</summary>
    public required string UsesText { get; init; }

    /// <summary>Gets the first and last use positions, such as <c>3-11</c>.</summary>
    public required string SpanText { get; init; }

    /// <summary>Gets short flag codes for the row.</summary>
    public required string FlagsText { get; init; }

    /// <summary>Gets the group text for the active grouping.</summary>
    public string GroupText { get; set; } = string.Empty;

    /// <summary>Gets lowercase text that the search matches.</summary>
    public required string SearchText { get; init; }
}
