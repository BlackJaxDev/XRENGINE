using XREngine.Rendering;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Caches the script text of the Workbench pipeline. The export runs again only when the pipeline or its command
/// generation changes.
/// </summary>
public sealed class RenderPipelineWorkbenchScript
{
    private RenderPipeline? _pipeline;
    private ulong _commandGeneration = ulong.MaxValue;

    /// <summary>Gets the exported script, or an empty string.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Gets the export failure text, or an empty string.</summary>
    public string Error { get; private set; } = string.Empty;

    /// <summary>Gets the lines of <see cref="Text"/>. The view draws them with a list clipper.</summary>
    public IReadOnlyList<string> Lines { get; private set; } = [];

    /// <summary>Gets the number of lines of <see cref="Text"/> as text.</summary>
    public string LineCountText { get; private set; } = string.Empty;

    /// <summary>Exports the command chain when the pipeline or its command generation changed.</summary>
    public void Update(RenderPipelineWorkbenchModel model)
    {
        RenderPipeline? pipeline = model.Pipeline;
        if (ReferenceEquals(pipeline, _pipeline) && model.CommandGeneration == _commandGeneration)
            return;

        _pipeline = pipeline;
        _commandGeneration = model.CommandGeneration;
        Text = string.Empty;
        Error = string.Empty;
        LineCountText = string.Empty;
        Lines = [];
        if (pipeline?.CommandChain is not { } chain)
            return;

        try
        {
            Text = RenderPipelineScript.Export(chain);
            Lines = Text.ReplaceLineEndings("\n").Split('\n');
            LineCountText = string.Concat(RenderPipelineWorkbenchText.FormatCount(Lines.Count), " lines");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            Error = string.Concat("The export failed: ", ex.Message);
        }
    }
}
