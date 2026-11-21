using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.Resources;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Validates draft revisions on a background task. Each request works on a private copy of the draft state, so the
/// task never reads objects that the editor changes. A result for an older revision is dropped.
/// </summary>
public sealed class RenderPipelineDraftValidation
{
    private readonly Func<Func<Result>, Task<Result>> _runner;
    private int _requestedRevision;
    private RenderPipelineDraft? _requestedDraft;
    private Result? _latest;

    /// <summary>Creates a validation that runs on the thread pool.</summary>
    public RenderPipelineDraftValidation()
        : this(static work => Task.Run(work)) { }

    /// <summary>Creates a validation with a custom runner. Tests pass a runner that runs the work at once.</summary>
    public RenderPipelineDraftValidation(Func<Func<Result>, Task<Result>> runner)
        => _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <summary>One finished validation.</summary>
    /// <param name="Draft">The draft that was checked.</param>
    /// <param name="Revision">The draft revision that was checked.</param>
    /// <param name="Preview">The preview of the copied state.</param>
    /// <param name="Report">The validation report. Command problems point to commands of the copied state.</param>
    public sealed record Result(RenderPipelineDraft Draft, int Revision, RenderPipelineDraftPreview Preview, RenderPipelineValidationReport Report)
    {
        /// <summary>Gets a value that is true when the report has no errors.</summary>
        public bool CanApply => Report.ErrorCount == 0;
    }

    /// <summary>Gets the newest result, or null. It can be older than the current draft revision.</summary>
    public Result? Latest => Volatile.Read(ref _latest);

    /// <summary>Gets the number of validations that started.</summary>
    public int RequestCount { get; private set; }

    /// <summary>Returns the newest result when it matches the current revision of <paramref name="draft"/>.</summary>
    public bool TryGetCurrent(RenderPipelineDraft draft, out Result result)
    {
        Result? latest = Latest;
        if (latest is not null && ReferenceEquals(latest.Draft, draft) && latest.Revision == draft.Revision)
        {
            result = latest;
            return true;
        }

        result = null!;
        return false;
    }

    /// <summary>
    /// Starts a validation of the current draft revision when no validation for it started before.
    /// </summary>
    /// <returns>The task of the started validation, or null when the revision has a validation.</returns>
    public Task? Request(RenderPipelineDraft draft, RenderPipelineResourceProfile profile, RendererBackendId backend)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (ReferenceEquals(_requestedDraft, draft) && _requestedRevision == draft.Revision)
            return null;

        _requestedDraft = draft;
        _requestedRevision = draft.Revision;
        RequestCount++;

        int revision = draft.Revision;
        RenderPipelineDraftState copy = draft.State.Clone();
        CustomRenderPipeline pipeline = draft.Pipeline;
        return _runner(() =>
        {
            RenderPipelineDraftPreview preview = RenderPipelineDraftPreview.Build(copy, revision, profile);
            RenderPipelineValidationReport report = preview.Validate(pipeline, backend, profile);
            return new Result(draft, revision, preview, report);
        }).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully)
                Publish(task.Result);
            else if (task.Exception is { } ex)
                Debug.LogWarning($"[Render Pipeline Workbench] Draft validation failed: {ex.GetBaseException().Message}");
        }, TaskScheduler.Default);
    }

    /// <summary>Forgets the requested revision, so the next request validates again.</summary>
    public void Reset()
    {
        _requestedDraft = null;
        _requestedRevision = 0;
        Volatile.Write(ref _latest, null);
    }

    private void Publish(Result result)
    {
        // A slow task for an old revision must not replace a newer result.
        while (true)
        {
            Result? current = Volatile.Read(ref _latest);
            if (current is not null && ReferenceEquals(current.Draft, result.Draft) && current.Revision > result.Revision)
                return;
            if (!ReferenceEquals(result.Draft, Volatile.Read(ref _requestedDraft)))
                return;
            if (Interlocked.CompareExchange(ref _latest, result, current) == current)
                return;
        }
    }
}
