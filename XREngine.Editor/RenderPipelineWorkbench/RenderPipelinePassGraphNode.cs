using System.Numerics;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// One cached pass node of the Passes view.
/// </summary>
public sealed class RenderPipelinePassGraphNode
{
    /// <summary>Gets the pass metadata.</summary>
    public required RenderPassMetadata Metadata { get; init; }

    /// <summary>Gets the pass index.</summary>
    public int PassIndex => Metadata.PassIndex;

    /// <summary>Gets the position of the pass in topological order.</summary>
    public required int TopologicalOrder { get; init; }

    /// <summary>Gets <see cref="TopologicalOrder"/> as text.</summary>
    public required string OrderText { get; init; }

    /// <summary>Gets the layout column (the longest dependency chain that ends at this pass).</summary>
    public int Column { get; internal set; }

    /// <summary>Gets the layout row inside the stage band.</summary>
    public int Row { get; internal set; }

    /// <summary>Gets the top-left canvas position.</summary>
    public Vector2 Position { get; set; }

    /// <summary>Gets the title text.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the subtitle text with the stage and use counts.</summary>
    public required string Subtitle { get; init; }

    /// <summary>Gets the attachment summary text.</summary>
    public required string AttachmentSummary { get; init; }

    /// <summary>Gets the names of the passes that this pass depends on, joined with commas.</summary>
    public string ProducersText { get; internal set; } = string.Empty;

    /// <summary>Gets the names of the passes that depend on this pass, joined with commas.</summary>
    public string ConsumersText { get; internal set; } = string.Empty;

    /// <summary>Gets the node id of the owning command, or 0 when the pipeline declared the pass.</summary>
    public int OwnerCommandId { get; init; }

    /// <summary>Gets the indices into <see cref="RenderPipelinePassGraphLayout.Edges"/> of edges that end at this node.</summary>
    public List<int> IncomingEdges { get; } = [];

    /// <summary>Gets the indices into <see cref="RenderPipelinePassGraphLayout.Edges"/> of edges that start at this node.</summary>
    public List<int> OutgoingEdges { get; } = [];
}
