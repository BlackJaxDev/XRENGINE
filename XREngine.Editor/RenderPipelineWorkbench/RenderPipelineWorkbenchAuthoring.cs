using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.Resources;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Owns the authoring state of the Workbench: the open draft, its preview for the views, its background validation,
/// and the command palette. Code pipelines have no draft.
/// </summary>
public sealed class RenderPipelineWorkbenchAuthoring
{
    private RenderPipelineDraftPreview? _preview;
    private int _autoAppliedRevision;
    private int _observedAppliedRevision;
    private string? _observedFailure;

    /// <summary>Gets the open draft, or null.</summary>
    public RenderPipelineDraft? Draft { get; private set; }

    /// <summary>Gets the background validation of the draft.</summary>
    public RenderPipelineDraftValidation Validation { get; } = new();

    /// <summary>Gets the command palette state.</summary>
    public RenderPipelineCommandPalette Palette { get; } = new();

    /// <summary>Gets the resource names that blocked the last delete.</summary>
    public List<string> BlockedDeleteReferences { get; } = [];

    /// <summary>Gets or sets the text of the resource rename field.</summary>
    public string RenameBuffer { get; set; } = string.Empty;

    /// <summary>Gets or sets the resource that the rename field edits.</summary>
    public string? RenameTarget { get; set; }

    /// <summary>Gets or sets the pass that the "Depends on" picker adds an edge to.</summary>
    public int DependencyPassIndex { get; set; } = -1;

    /// <summary>Gets a value that is true when <paramref name="pipeline"/> can have a draft.</summary>
    public static bool CanAuthor(RenderPipeline? pipeline)
        => pipeline is CustomRenderPipeline;

    /// <summary>Returns true when a draft for <paramref name="pipeline"/> is open.</summary>
    public bool IsEditing(RenderPipeline? pipeline)
        => Draft is { IsClosed: false } draft && ReferenceEquals(draft.Pipeline, pipeline);

    /// <summary>Opens a draft for <paramref name="pipeline"/>. An open draft for another pipeline closes first.</summary>
    public RenderPipelineDraft BeginEdit(CustomRenderPipeline pipeline)
    {
        if (Draft is { IsClosed: false } open && ReferenceEquals(open.Pipeline, pipeline))
            return open;

        Close();
        Draft = new RenderPipelineDraft(pipeline);
        _preview = null;
        _autoAppliedRevision = 0;
        _observedAppliedRevision = Draft.AppliedRevision;
        _observedFailure = null;
        Validation.Reset();
        return Draft;
    }

    /// <summary>Closes the open draft. Edits that Apply did not write are lost.</summary>
    public void Close()
    {
        Draft?.Close();
        Draft = null;
        _preview = null;
        Validation.Reset();
    }

    /// <summary>
    /// Returns the preview of the current draft revision. The preview is built on the calling thread when the revision
    /// changed, because the views need it in the same frame as the edit.
    /// </summary>
    public RenderPipelineDraftPreview GetPreview(RenderPipelineResourceProfile profile)
    {
        RenderPipelineDraft draft = Draft ?? throw new InvalidOperationException("No draft is open.");
        if (_preview is null || _preview.Revision != draft.Revision)
            _preview = RenderPipelineDraftPreview.Build(draft.State, draft.Revision, profile);
        return _preview;
    }

    /// <summary>
    /// Runs the per-frame work of an open draft: starts validation for a new revision, applies a valid revision when
    /// Auto-apply is on, and records a generation failure that follows an Apply.
    /// </summary>
    public void Update(XRRenderPipelineInstance? instance, RenderPipelineResourceProfile profile, RendererBackendId backend)
    {
        if (Draft is not { IsClosed: false } draft)
            return;

        Validation.Request(draft, profile, backend);
        if (draft.AutoApply &&
            draft.IsDirty &&
            !draft.IsApplyPending &&
            _autoAppliedRevision != draft.Revision &&
            Validation.TryGetCurrent(draft, out RenderPipelineDraftValidation.Result result) &&
            result.CanApply)
        {
            _autoAppliedRevision = draft.Revision;
            draft.Apply();
        }

        ObserveGenerationFailure(draft, instance);
    }

    /// <summary>Returns true when the current revision validated with no errors.</summary>
    public bool CanApply(out RenderPipelineValidationReport? report)
    {
        report = null;
        if (Draft is not { IsClosed: false } draft || !Validation.TryGetCurrent(draft, out RenderPipelineDraftValidation.Result result))
            return false;

        report = result.Report;
        return result.CanApply;
    }

    private void ObserveGenerationFailure(RenderPipelineDraft draft, XRRenderPipelineInstance? instance)
    {
        // A finished Apply builds a pending generation. When that generation fails, the instance reports the reason and
        // keeps the active generation. The draft keeps its edits and shows the reason.
        if (draft.AppliedRevision != _observedAppliedRevision)
        {
            _observedAppliedRevision = draft.AppliedRevision;
            _observedFailure = instance?.LastResourceGenerationFailure;
            return;
        }

        string? failure = instance?.LastResourceGenerationFailure;
        if (failure is { Length: > 0 } && !string.Equals(failure, _observedFailure, StringComparison.Ordinal))
        {
            _observedFailure = failure;
            draft.ReportApplyFailure(failure);
        }
    }
}
