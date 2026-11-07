using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>One row of the command palette: a snippet or a command type. The texts are built once per row.</summary>
public sealed class RenderPipelinePaletteItem
{
    private RenderPipelinePaletteItem(string label, string category, string description, string detail, RenderCommandCatalogEntry? command, RenderPipelineSnippet? snippet)
    {
        Label = label;
        Category = category;
        Description = description;
        Detail = detail;
        Command = command;
        Snippet = snippet;
    }

    /// <summary>Gets the row label.</summary>
    public string Label { get; }

    /// <summary>Gets the category text.</summary>
    public string Category { get; }

    /// <summary>Gets the description, or an empty string.</summary>
    public string Description { get; }

    /// <summary>Gets the extra line: the stages that the command reads, or the snippet note.</summary>
    public string Detail { get; }

    /// <summary>Gets the command entry, or null for a snippet.</summary>
    public RenderCommandCatalogEntry? Command { get; }

    /// <summary>Gets the snippet, or null for a command.</summary>
    public RenderPipelineSnippet? Snippet { get; }

    /// <summary>Creates the row of a command type.</summary>
    public static RenderPipelinePaletteItem ForCommand(RenderCommandCatalogEntry entry)
    {
        RenderCommandInfo info = entry.Info;
        string gpu = info.DoesGpuWork ? "Declares GPU passes." : "No GPU pass.";
        string detail = info.ConsumesStages.Count == 0
            ? gpu
            : string.Concat(gpu, " Reads stages: ", string.Join(", ", info.ConsumesStages));
        return new RenderPipelinePaletteItem(info.DisplayName, info.Category.ToString(), info.Description, detail, entry, null);
    }

    /// <summary>Creates the row of a snippet.</summary>
    public static RenderPipelinePaletteItem ForSnippet(RenderPipelineSnippet snippet)
        => new(snippet.Name, "Snippet", snippet.Description, "Inserts several commands and the declarations they need.", null, snippet);
}
