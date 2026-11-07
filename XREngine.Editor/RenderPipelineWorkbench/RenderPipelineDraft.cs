using XREngine.Rendering;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// The Workbench copy of a custom render pipeline. Edits change the draft, never the live pipeline. Apply writes a
/// copy of the draft to the pipeline on the render thread.
/// </summary>
/// <remarks>
/// Each edit records one undo step. The step keeps a copy of the state from before the edit. Undo makes that copy
/// current and keeps the edited state for redo, so redo restores the same objects and the selection stays valid.
/// </remarks>
public sealed class RenderPipelineDraft
{
    private readonly Action<Action> _dispatchToRenderThread;
    private RenderPipelineDraftState _state;
    private int _revision = 1;
    private int _appliedRevision;
    private int _pendingApplyRevision;
    private string? _applyFailure;

    /// <summary>Creates a draft from the authored data of <paramref name="pipeline"/>.</summary>
    /// <param name="pipeline">The pipeline to edit.</param>
    /// <param name="dispatchToRenderThread">Runs an action on the render thread. Null uses the engine render queue.</param>
    public RenderPipelineDraft(CustomRenderPipeline pipeline, Action<Action>? dispatchToRenderThread = null)
    {
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _dispatchToRenderThread = dispatchToRenderThread ?? Engine.EnqueueRenderThreadTask;
        _state = RenderPipelineDraftState.From(pipeline);
        _appliedRevision = _revision;
    }

    /// <summary>Gets the pipeline that this draft edits.</summary>
    public CustomRenderPipeline Pipeline { get; }

    /// <summary>Gets the current draft data. Change it only through <see cref="Edit"/>.</summary>
    public RenderPipelineDraftState State => _state;

    /// <summary>Gets a number that changes after each edit, undo, and redo.</summary>
    public int Revision => _revision;

    /// <summary>Gets the revision that the last finished Apply wrote.</summary>
    public int AppliedRevision => _appliedRevision;

    /// <summary>Gets a value that is true when the draft has edits that Apply did not write.</summary>
    public bool IsDirty => _revision != _appliedRevision;

    /// <summary>Gets a value that is true while an Apply waits for the render thread.</summary>
    public bool IsApplyPending => _pendingApplyRevision != 0;

    /// <summary>Gets the reason the last Apply failed, or null.</summary>
    public string? ApplyFailure => _applyFailure;

    /// <summary>Gets a value that is true after <see cref="Close"/>. Undo steps of a closed draft do nothing.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Gets or sets a value that makes the Workbench apply each draft revision that has no errors.</summary>
    public bool AutoApply { get; set; }

    /// <summary>Occurs after each edit, undo, and redo.</summary>
    public event Action<RenderPipelineDraft>? Changed;

    /// <summary>
    /// Runs <paramref name="mutate"/> on the draft state and records one undo step named <paramref name="description"/>.
    /// </summary>
    /// <returns>False when the draft is closed or the edit threw. A failed edit restores the state from before it.</returns>
    public bool Edit(string description, Action<RenderPipelineDraftState> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (IsClosed)
            return false;

        RenderPipelineDraftState before = _state.Clone();
        try
        {
            mutate(_state);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _state = before;
            Debug.LogWarning($"[Render Pipeline Workbench] {description} failed: {ex.Message}");
            return false;
        }

        MarkChanged();
        RecordUndo(description, before);
        return true;
    }

    /// <summary>
    /// Runs <paramref name="mutate"/> and records one undo step when it returns true. A false result means that the
    /// edit did not apply and did not change the state, so no step is recorded.
    /// </summary>
    public bool TryEdit(string description, Func<RenderPipelineDraftState, bool> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (IsClosed)
            return false;

        RenderPipelineDraftState before = _state.Clone();
        if (!mutate(_state))
            return false;

        MarkChanged();
        RecordUndo(description, before);
        return true;
    }

    /// <summary>
    /// Marks the draft as changed after an edit that did not go through <see cref="Edit"/>, such as a property edit in
    /// the Inspector. Call <see cref="RecordEdit"/> when the edit gesture ends.
    /// </summary>
    public void MarkEdited()
    {
        if (!IsClosed)
            MarkChanged();
    }

    /// <summary>
    /// Records one undo step for an edit that already changed the state. <paramref name="before"/> is a copy of the
    /// state from before the edit.
    /// </summary>
    public void RecordEdit(string description, RenderPipelineDraftState before)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (!IsClosed)
            RecordUndo(description, before);
    }

    /// <summary>Replaces the draft with the current data of the pipeline. This step has no undo.</summary>
    public void Revert()
    {
        if (IsClosed)
            return;

        _state = RenderPipelineDraftState.From(Pipeline);
        MarkChanged();
        _appliedRevision = _revision;
    }

    /// <summary>
    /// Writes a copy of the current revision to the pipeline on the render thread. The draft stays open. When the
    /// write throws, <see cref="ApplyFailure"/> holds the reason and the active generation keeps rendering.
    /// </summary>
    /// <returns>False when the draft is closed or an Apply is pending.</returns>
    public bool Apply()
    {
        if (IsClosed || IsApplyPending)
            return false;

        int revision = _revision;
        RenderPipelineDraftState copy = _state.Clone();
        _pendingApplyRevision = revision;
        _applyFailure = null;
        _dispatchToRenderThread(() => ApplyOnRenderThread(copy, revision));
        return true;
    }

    /// <summary>
    /// Records a failure that the render thread reported after an Apply, such as a pending generation that did not
    /// commit. The draft stays dirty, so the user can fix it and apply again.
    /// </summary>
    public void ReportApplyFailure(string reason)
    {
        _applyFailure = reason;
        _appliedRevision = 0;
    }

    /// <summary>Closes the draft. Later undo and redo steps of this draft do nothing.</summary>
    public void Close()
        => IsClosed = true;

    private void ApplyOnRenderThread(RenderPipelineDraftState copy, int revision)
    {
        try
        {
            copy.WriteTo(Pipeline);
            _appliedRevision = revision;
        }
        catch (Exception ex)
        {
            _applyFailure = ex.Message;
            Debug.LogWarning($"[Render Pipeline Workbench] Apply failed: {ex.Message}");
        }
        finally
        {
            _pendingApplyRevision = 0;
        }
    }

    private void RecordUndo(string description, RenderPipelineDraftState before)
    {
        RenderPipelineDraftState? redoState = null;
        using IDisposable interaction = Undo.BeginUserInteraction();
        Undo.RecordStructuralChange(
            description,
            undoAction: () =>
            {
                if (IsClosed)
                    return;

                redoState = _state;
                SetState(before);
            },
            redoAction: () =>
            {
                if (IsClosed || redoState is null)
                    return;

                SetState(redoState);
            });
    }

    private void SetState(RenderPipelineDraftState state)
    {
        _state = state;
        MarkChanged();
    }

    private void MarkChanged()
    {
        _revision++;
        if (_revision == 0)
            _revision = 1;
        Changed?.Invoke(this);
    }
}
