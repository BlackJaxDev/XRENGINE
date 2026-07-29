using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Fixed-capacity per-view visibility-state owner and GPU dispatch planner.
/// It produces capacities and buffer offsets only; visible counts stay on the
/// GPU and late work consumes only the deferred-candidate buffer.
/// </summary>
public sealed class AdvancedVisibilityPlanner
{
    public const uint ThreadGroupSize = 256u;

    private readonly ulong[] _viewHistoryKeys;
    private readonly uint[] _viewWidths;
    private readonly uint[] _viewHeights;
    private readonly uint[] _viewHistoryGenerations;
    private readonly AdvancedVisibilityPersistentRecord[] _records;
    private readonly EAdvancedVisibilityPreparationFlags[] _candidateFlags;
    private readonly int _drawCapacity;
    private ulong _frameId;

    public AdvancedVisibilityPlanner(int maximumViews, int drawCapacity)
    {
        if (maximumViews <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumViews));
        if (drawCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(drawCapacity));

        _viewHistoryKeys = new ulong[maximumViews];
        _viewWidths = new uint[maximumViews];
        _viewHeights = new uint[maximumViews];
        _viewHistoryGenerations = new uint[maximumViews];
        _records = new AdvancedVisibilityPersistentRecord[
            checked(maximumViews * drawCapacity)];
        _candidateFlags =
            new EAdvancedVisibilityPreparationFlags[drawCapacity];
        _drawCapacity = drawCapacity;
    }

    public int MaximumViews => _viewHistoryKeys.Length;
    public int DrawCapacity => _drawCapacity;
    public ulong SynchronousReadbackCount => 0UL;

    public void BeginFrame(ulong frameId)
        => _frameId = frameId;

    public AdvancedVisibilityDispatchPlan BuildPlan(
        int viewSlot,
        in AdvancedDepthPyramidContract depthPyramid,
        ReadOnlySpan<AdvancedVisibilityCandidate> candidates,
        uint earlyIndirectArgumentOffset,
        uint deferredCandidateOffset,
        uint lateIndirectArgumentOffset,
        uint persistentStateOffset,
        uint gpuCounterOffset)
    {
        ValidateViewSlot(viewSlot);
        if (candidates.Length > _drawCapacity)
            throw new ArgumentOutOfRangeException(nameof(candidates));
        if (depthPyramid.ViewHistoryKey == 0UL ||
            depthPyramid.Width == 0u ||
            depthPyramid.Height == 0u)
        {
            throw new ArgumentException(
                "Visibility preparation requires a stable non-empty view contract.",
                nameof(depthPyramid));
        }

        bool newView =
            _viewHistoryKeys[viewSlot] != depthPyramid.ViewHistoryKey;
        bool resized =
            !newView &&
            (_viewWidths[viewSlot] != depthPyramid.Width ||
             _viewHeights[viewSlot] != depthPyramid.Height);
        if (newView || resized)
        {
            _viewHistoryKeys[viewSlot] = depthPyramid.ViewHistoryKey;
            _viewWidths[viewSlot] = depthPyramid.Width;
            _viewHeights[viewSlot] = depthPyramid.Height;
            _viewHistoryGenerations[viewSlot]++;
            ClearViewRecords(viewSlot);
        }

        int viewBase = checked(viewSlot * _drawCapacity);
        for (int candidateIndex = 0;
             candidateIndex < candidates.Length;
             candidateIndex++)
        {
            AdvancedVisibilityCandidate candidate = candidates[candidateIndex];
            ValidateDraw(candidate.Draw);
            int recordIndex = checked(viewBase + (int)candidate.Draw.Index - 1);
            AdvancedVisibilityPersistentRecord previous =
                _records[recordIndex];

            EAdvancedVisibilityPreparationFlags flags = candidate.Flags;
            bool newRecord = previous.Draw != candidate.Draw;
            if (newRecord)
                flags |= EAdvancedVisibilityPreparationFlags.NewRecord;
            if (resized)
                flags |= EAdvancedVisibilityPreparationFlags.ResizedView;
            if (!depthPyramid.PreviousValid)
                flags |= EAdvancedVisibilityPreparationFlags.InvalidHistory;
            if ((flags &
                 (EAdvancedVisibilityPreparationFlags.NewRecord |
                  EAdvancedVisibilityPreparationFlags.ResizedView |
                  EAdvancedVisibilityPreparationFlags.InvalidHistory |
                  EAdvancedVisibilityPreparationFlags.Uncertain)) != 0)
            {
                flags |=
                    EAdvancedVisibilityPreparationFlags.ConservativeVisible;
            }

            _candidateFlags[candidateIndex] = flags;
            _records[recordIndex] = previous with
            {
                Draw = candidate.Draw,
                LastTestedFrame = _frameId,
                DepthPyramidGeneration = depthPyramid.PreviousGeneration,
                Flags = flags,
            };
        }

        uint candidateCapacity = checked((uint)candidates.Length);
        uint groups = candidateCapacity == 0u
            ? 0u
            : checked((candidateCapacity + ThreadGroupSize - 1u) /
                ThreadGroupSize);
        return new AdvancedVisibilityDispatchPlan(
            depthPyramid.ViewHistoryKey,
            candidateCapacity,
            EarlyWorkGroupCount: groups,
            LateWorkGroupCount: groups,
            earlyIndirectArgumentOffset,
            deferredCandidateOffset,
            lateIndirectArgumentOffset,
            persistentStateOffset,
            gpuCounterOffset,
            UsesPreviousDepthPyramid: depthPyramid.PreviousValid,
            LateTestsDeferredOnly: true,
            RequiresCpuCount: false,
            RequiresReadback: false);
    }

    public ReadOnlySpan<EAdvancedVisibilityPreparationFlags>
        GetCandidatePreparationFlags(int count)
    {
        if ((uint)count > (uint)_candidateFlags.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        return _candidateFlags.AsSpan(0, count);
    }

    public void ApplyGpuResultsForValidation(
        int viewSlot,
        ReadOnlySpan<AdvancedGpuVisibilityResult> results,
        uint currentDepthPyramidGeneration)
    {
        ValidateViewSlot(viewSlot);
        int viewBase = checked(viewSlot * _drawCapacity);
        for (int i = 0; i < results.Length; i++)
        {
            AdvancedGpuVisibilityResult result = results[i];
            ValidateDraw(result.Draw);
            int recordIndex = checked(viewBase + (int)result.Draw.Index - 1);
            AdvancedVisibilityPersistentRecord record =
                _records[recordIndex];
            record.Draw = result.Draw;
            record.LastTestedFrame = _frameId;
            record.DepthPyramidGeneration = currentDepthPyramidGeneration;
            record.Flags = result.Flags;
            if ((result.Flags &
                 (EAdvancedVisibilityPreparationFlags.EarlyVisible |
                  EAdvancedVisibilityPreparationFlags.LateVisible |
                  EAdvancedVisibilityPreparationFlags.ConservativeVisible)) != 0)
            {
                record.LastVisibleFrame = _frameId;
            }
            _records[recordIndex] = record;
        }
    }

    public bool TryGetPersistentRecord(
        int viewSlot,
        AdvancedGpuHandle draw,
        out AdvancedVisibilityPersistentRecord record)
    {
        ValidateViewSlot(viewSlot);
        ValidateDraw(draw);
        record = _records[
            checked(viewSlot * _drawCapacity + (int)draw.Index - 1)];
        return record.Draw == draw;
    }

    private void ClearViewRecords(int viewSlot)
        => Array.Clear(
            _records,
            checked(viewSlot * _drawCapacity),
            _drawCapacity);

    private void ValidateViewSlot(int viewSlot)
    {
        if ((uint)viewSlot >= (uint)_viewHistoryKeys.Length)
            throw new ArgumentOutOfRangeException(nameof(viewSlot));
    }

    private void ValidateDraw(AdvancedGpuHandle draw)
    {
        if (!draw.IsValid ||
            draw.Index == 0u ||
            draw.Index > (uint)_drawCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(draw),
                "Visibility draw handles must fit the stable draw table.");
        }
    }
}
