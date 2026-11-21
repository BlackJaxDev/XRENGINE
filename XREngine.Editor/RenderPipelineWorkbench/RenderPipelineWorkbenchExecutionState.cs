using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Computes which outline rows executed in the newest traced frame, from the branch trace of a pipeline instance.
/// The state reuses its storage and recomputes only when the outline or the trace changes.
/// </summary>
public sealed class RenderPipelineWorkbenchExecutionState
{
    private ERenderPipelineRowExecution[] _rows = [];
    private readonly Stack<int> _parents = new(32);
    private int _outlineBuild = -1;
    private int _traceVersion = -1;
    private RenderPipelineBranchTrace? _trace;
    private RenderPipelineWorkbenchModel? _model;

    /// <summary>Gets the number of rows that did not execute in the newest traced frame.</summary>
    public int SkippedCount { get; private set; }

    /// <summary>Gets the execution state of the outline row at <paramref name="rowIndex"/>.</summary>
    public ERenderPipelineRowExecution Get(int rowIndex)
        => (uint)rowIndex < (uint)_rows.Length ? _rows[rowIndex] : ERenderPipelineRowExecution.Unknown;

    /// <summary>
    /// Recomputes the state when the outline of <paramref name="model"/> or the trace of its instance changed.
    /// A frozen model keeps the last state.
    /// </summary>
    public void Update(RenderPipelineWorkbenchModel model)
    {
        if (model.IsFrozen && ReferenceEquals(model, _model))
            return;

        RenderPipelineBranchTrace? trace = model.Instance?.BranchTrace;
        int traceVersion = trace?.Version ?? -1;
        if (ReferenceEquals(model, _model) &&
            ReferenceEquals(trace, _trace) &&
            model.OutlineBuildCount == _outlineBuild &&
            traceVersion == _traceVersion)
        {
            return;
        }

        _model = model;
        _trace = trace;
        _outlineBuild = model.OutlineBuildCount;
        _traceVersion = traceVersion;
        Compute(model.Outline, trace);
    }

    private void Compute(List<RenderPipelineOutlineRow> rows, RenderPipelineBranchTrace? trace)
    {
        if (_rows.Length < rows.Count)
            _rows = new ERenderPipelineRowExecution[Math.Max(rows.Count, _rows.Length * 2)];

        Array.Clear(_rows, 0, _rows.Length);
        SkippedCount = 0;
        if (trace is null || !trace.HasRecords)
            return;

        ulong latest = trace.LatestFrameId;
        _parents.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            RenderPipelineOutlineRow row = rows[i];
            while (_parents.Count > 0 && rows[_parents.Peek()].Depth >= row.Depth)
                _parents.Pop();

            int parent = _parents.Count > 0 ? _parents.Peek() : -1;
            ERenderPipelineRowExecution state = parent >= 0 && _rows[parent] == ERenderPipelineRowExecution.Skipped
                ? ERenderPipelineRowExecution.Skipped
                : Evaluate(rows, row, parent, trace, latest);

            _rows[i] = state;
            if (state == ERenderPipelineRowExecution.Skipped)
                SkippedCount++;
            _parents.Push(i);
        }
    }

    private static ERenderPipelineRowExecution Evaluate(
        List<RenderPipelineOutlineRow> rows,
        RenderPipelineOutlineRow row,
        int parent,
        RenderPipelineBranchTrace trace,
        ulong latest)
    {
        if (row.Kind == ERenderPipelineOutlineRowKind.Command)
        {
            // A branch command with an old record did not run in the newest frame.
            if (row.Command is not null && trace.TryGet(row.Command, out RenderPipelineBranchRecord own))
                return own.FrameId == latest ? ERenderPipelineRowExecution.Executed : ERenderPipelineRowExecution.Skipped;
            return ERenderPipelineRowExecution.Unknown;
        }

        if (parent < 0 || rows[parent].Command is not ViewportRenderCommand owner)
            return ERenderPipelineRowExecution.Unknown;

        if (!trace.TryGet(owner, out RenderPipelineBranchRecord record))
            return ERenderPipelineRowExecution.Unknown;

        return record.FrameId == latest && ReferenceEquals(record.Container, row.Container)
            ? ERenderPipelineRowExecution.Executed
            : ERenderPipelineRowExecution.Skipped;
    }
}
