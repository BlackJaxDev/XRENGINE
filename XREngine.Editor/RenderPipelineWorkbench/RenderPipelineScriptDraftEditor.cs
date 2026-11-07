using XREngine.Rendering;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// The edit buffer of the Script view for an authored pipeline. The buffer holds the full <c>.xrs</c> text of the
/// draft. <see cref="Apply"/> loads the text and replaces the draft state as one undo step. A text with errors leaves
/// the draft unchanged and reports each error with its line and column.
/// </summary>
public sealed class RenderPipelineScriptDraftEditor
{
    private IReadOnlyList<RenderPipelineScriptProblem> _problems = [];

    /// <summary>Gets or sets the text in the edit buffer.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets whether the Script view shows the edit buffer.</summary>
    public bool IsEditing { get; private set; }

    /// <summary>Gets the draft that the buffer edits, or null.</summary>
    public RenderPipelineDraft? Draft { get; private set; }

    /// <summary>Gets the draft revision that the buffer text came from or went to.</summary>
    public int SyncedRevision { get; private set; } = -1;

    /// <summary>Gets the problems of the last <see cref="Apply"/>.</summary>
    public IReadOnlyList<RenderPipelineScriptProblem> Problems => _problems;

    /// <summary>Gets whether the draft changed after the buffer text came from it.</summary>
    public bool IsDraftNewer => Draft is { } draft && draft.Revision != SyncedRevision;

    /// <summary>Starts to edit <paramref name="draft"/> with its current text.</summary>
    public void Begin(RenderPipelineDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Draft = draft;
        Text = WriteState(draft.State);
        SyncedRevision = draft.Revision;
        _problems = [];
        IsEditing = true;
    }

    /// <summary>Replaces the buffer with the current text of the draft. Unapplied buffer changes are lost.</summary>
    public void Reload()
    {
        if (Draft is { } draft)
            Begin(draft);
    }

    /// <summary>Stops editing and drops the buffer.</summary>
    public void End()
    {
        IsEditing = false;
        Draft = null;
        Text = string.Empty;
        SyncedRevision = -1;
        _problems = [];
    }

    /// <summary>
    /// Loads <see cref="Text"/> and, when it has no errors, replaces the draft state with it as one undo step named
    /// "Apply script". Unconverted items are warnings: the draft keeps the rest, and validation reports the gaps.
    /// </summary>
    /// <returns>True when the draft changed.</returns>
    public bool Apply()
    {
        if (Draft is not { } draft)
            return false;

        if (!TryLoadState(Text, out RenderPipelineDraftState? loaded, out _problems))
            return false;

        bool changed = draft.TryEdit("Apply script", state =>
        {
            state.Commands = loaded!.Commands;
            state.RenderPasses = loaded.RenderPasses;
            state.PassDependencies = loaded.PassDependencies;
            state.Resources = loaded.Resources;
            state.Stages = loaded.Stages;
            return true;
        });

        if (changed)
            SyncedRevision = draft.Revision;
        return changed;
    }

    /// <summary>Writes <paramref name="state"/> as a standalone script.</summary>
    public static string WriteState(RenderPipelineDraftState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        CustomRenderPipeline snapshot = new();
        state.WriteTo(snapshot);
        return RenderPipelineScriptWriter.Write(snapshot);
    }

    /// <summary>
    /// Loads <paramref name="text"/> as a standalone script and reads its draft state. A derived script, a parse
    /// error, or a load error fails and fills <paramref name="problems"/>.
    /// </summary>
    public static bool TryLoadState(string text, out RenderPipelineDraftState? state, out IReadOnlyList<RenderPipelineScriptProblem> problems)
    {
        state = null;
        RenderPipelineScriptLoadResult result = RenderPipelineScriptLoader.Load(text ?? string.Empty);
        problems = result.Problems;
        if (result.Document.HasBlockingErrors || result.Pipeline is null)
            return false;

        if (result.Document.IsDerived || result.Pipeline is not CustomRenderPipeline custom)
        {
            problems = [.. problems, new RenderPipelineScriptProblem(
                ERenderPipelineScriptProblemSeverity.Error,
                1,
                1,
                "The Script view edits an authored pipeline. Remove the derive header, or open the derived script as a file.")];
            return false;
        }

        state = RenderPipelineDraftState.From(custom);
        return true;
    }
}
