using System.Numerics;
using XREngine.Animation;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.Editor;

public partial class AnimationPanel
{
    private AnimationPlotPointerSnapshot? _activePlotDrag;
    private ulong _dragViewRevision;
    private AnimationPlotPointerSnapshot? _activePlotScrub;

    private void PublishPlotSnapshot(AnimationClip clip)
    {
        if (_plotInput is not { IsActiveInHierarchy: true } input ||
            !ReferenceEquals(CurrentClip(), clip) || _projectionDirty)
            return;
        int laneCount = _graphView ? (_selectedMember?.Animation is null ? 0 : 1) :
            Math.Min(5, _visiblePlotMembers.Count);
        AnimationPlotPointerSnapshot.Lane[] lanes = new AnimationPlotPointerSnapshot.Lane[laneCount];
        if (_graphView && laneCount != 0)
            lanes[0] = new(_selectedMember!, _selectedMember!.Animation,
                6, _plottedHeight - 6);
        else
        {
            float laneHeight = laneCount == 0 ? 0 : (_plottedHeight - 34) / laneCount;
            for (int index = 0; index < laneCount; index++)
                lanes[index] = new(_visiblePlotMembers[index],
                    _visiblePlotMembers[index].Animation,
                    6 + index * laneHeight, 6 + (index + 1) * laneHeight);
        }
        AnimationPlotPointerSnapshot.KeyHit[] keys =
            new AnimationPlotPointerSnapshot.KeyHit[_plotKeyHits.Count];
        for (int index = 0; index < keys.Length; index++)
        {
            var hit = _plotKeyHits[index];
            keys[index] = new(hit.Member, hit.Key, hit.Track,
                hit.Second, hit.Point);
        }
        _plotSnapshot = new(this, input, clip, _sourceRevision, _plotViewRevision,
            _plotBuildRevision,
            _plottedWidth, _plottedHeight, _plottedStart, _plottedDuration,
            _graphView, lanes, keys);
        input.BoundableTransform.RecordUIChange(
            UIChangeDomain.Visual | UIChangeDomain.HitTest);
        if (!_plotValidator() ||
            _plotSnapshot is not { } published ||
            !ReferenceEquals(published.InputOwner, input) ||
            _plot?.Root.GetTransformAs<UIBoundableTransform>(true)
                is not { } root)
            return;
        root.Visibility = EVisibility.Visible;
        _plotBuildHidden = false;
        if (!IsPlotSourceCurrent(published) ||
            published.BuildRevision != _plotBuildRevision)
            _plotSnapshot = null;
    }

    internal object? CapturePlotPointerMetadata(Vector2 acceptedSize)
    {
        AnimationPlotPointerSnapshot? snapshot = _plotSnapshot;
        return snapshot is not null && IsPlotSourceCurrent(snapshot) &&
            ReferenceEquals(snapshot.InputOwner, _plotInput) &&
            !_plotBuilding && !_plotRefreshPending &&
            snapshot.BuildRevision == _plotBuildRevision &&
            snapshot.ViewRevision == _plotViewRevision &&
            MathF.Abs(snapshot.Width - acceptedSize.X) < 0.5f &&
            MathF.Abs(snapshot.Height - acceptedSize.Y) < 0.5f
            ? snapshot : null;
    }

    private bool IsPlotSourceCurrent(AnimationPlotPointerSnapshot snapshot)
        => !_destroying && IsActiveInHierarchy && !_projectionDirty &&
            ReferenceEquals(snapshot.PanelOwner, this) &&
            ReferenceEquals(_plotInput?.Panel, this) &&
            ReferenceEquals(CurrentClip(), snapshot.Clip) &&
            ReferenceEquals(_displayedClip, snapshot.Clip) &&
            snapshot.SourceRevision == _sourceRevision &&
            snapshot.ClipLength == snapshot.Clip.LengthInSeconds &&
            snapshot.SampleRate == snapshot.Clip.SampleRate;

    internal void ActivatePlotPointer(AnimationPlotPointerSnapshot snapshot,
        AnimationMember member, Vector2 point, bool doubleClick)
    {
        if (!IsPlotSourceCurrent(snapshot) ||
            !ReferenceEquals(_plotSnapshot, snapshot) ||
            snapshot.BuildRevision != _plotBuildRevision ||
            snapshot.ViewRevision != _plotViewRevision ||
            snapshot.Graph != _graphView ||
            !snapshot.HasCurrentLane(member) ||
            !AnimationClipAuthoringActions.ContainsMember(snapshot.Clip, member) ||
            (snapshot.Graph && !ReferenceEquals(member, _selectedMember)))
            return;
        ulong inputGeneration = _plotInput?.DispatchGeneration ?? 0;
        ulong viewRevision = _plotViewRevision;
        ulong buildRevision = _plotBuildRevision;
        AnimationMember? previousMember = _selectedMember;
        Keyframe? previousKey = _selectedKey;
        if (snapshot.TryHitKey(point, out var hit) &&
            ReferenceEquals(hit.Member, member) &&
            ReferenceEquals(hit.Key.OwningTrack, hit.Track) &&
            hit.Key.Second == hit.Second && MemberContainsKey(member, hit.Key))
        {
            if (CanContinuePlotEdit(snapshot, member, inputGeneration,
                    viewRevision, buildRevision, previousMember, previousKey))
                CommitPlotSelection(snapshot, member, hit.Key,
                    inputGeneration, viewRevision, buildRevision, _sourceRevision);
            return;
        }
        float insertTime = snapshot.TimeForX(point.X);
        bool occupied = doubleClick && HasCurrentPlotKeyNear(member, insertTime,
            snapshot.Duration * 8 / MathF.Max(1, snapshot.Width - 16));
        if (!CanStartPlotEdit(snapshot, member, inputGeneration, viewRevision,
                buildRevision, previousMember, previousKey))
            return;
        if (doubleClick && !occupied &&
            AnimationClipAuthoringActions.TryAddKey(snapshot.Clip,
                member, insertTime, out Keyframe? key))
        {
            if (CanContinuePlotEdit(snapshot, member, inputGeneration,
                    viewRevision, buildRevision, previousMember, previousKey))
                CommitPlotSelection(snapshot, member, key,
                    inputGeneration, viewRevision, buildRevision, _sourceRevision);
        }
        else if (CanContinuePlotEdit(snapshot, member, inputGeneration,
                viewRevision, buildRevision, previousMember, previousKey))
            CommitPlotSelection(snapshot, member, null,
                inputGeneration, viewRevision, buildRevision, _sourceRevision);
    }

    private bool CanStartPlotEdit(AnimationPlotPointerSnapshot snapshot,
        AnimationMember member, ulong inputGeneration, ulong viewRevision,
        ulong buildRevision, AnimationMember? previousMember, Keyframe? previousKey)
        => IsPlotSourceCurrent(snapshot) &&
            ReferenceEquals(_plotSnapshot, snapshot) &&
            snapshot.BuildRevision == _plotBuildRevision &&
            CanContinuePlotEdit(snapshot, member, inputGeneration,
                viewRevision, buildRevision, previousMember, previousKey);

    private bool CanContinuePlotEdit(AnimationPlotPointerSnapshot snapshot,
        AnimationMember member, ulong inputGeneration, ulong viewRevision,
        ulong buildRevision, AnimationMember? previousMember, Keyframe? previousKey)
        => !_destroying && IsActiveInHierarchy &&
            _plotInput?.DispatchGeneration == inputGeneration &&
            _plotViewRevision == viewRevision && _plotBuildRevision == buildRevision &&
            ReferenceEquals(_selectedMember, previousMember) &&
            ReferenceEquals(_selectedKey, previousKey) &&
            ReferenceEquals(CurrentClip(), snapshot.Clip) &&
            ReferenceEquals(_displayedClip, snapshot.Clip) &&
            snapshot.HasCurrentLane(member) &&
            AnimationClipAuthoringActions.ContainsMember(snapshot.Clip, member);

    private void CommitPlotSelection(AnimationPlotPointerSnapshot snapshot,
        AnimationMember member, Keyframe? key, ulong inputGeneration,
        ulong viewRevision, ulong buildRevision, ulong sourceRevision)
    {
        if (!CanCommitPlotSelection(snapshot, member, key, inputGeneration,
                viewRevision, buildRevision, sourceRevision))
            return;
        _selectedMember = member;
        _selectedKey = key;
        _authoringEpoch++;
        RefreshTimeInput();
        if (!CanCommitPlotSelection(snapshot, member, key, inputGeneration,
                viewRevision, buildRevision, sourceRevision) ||
            !ReferenceEquals(_selectedMember, member) ||
            !ReferenceEquals(_selectedKey, key))
            return;
        RefreshValueInput();
        if (CanCommitPlotSelection(snapshot, member, key, inputGeneration,
                viewRevision, buildRevision, sourceRevision) &&
            ReferenceEquals(_selectedMember, member) &&
            ReferenceEquals(_selectedKey, key))
            RefreshRows();
    }

    private bool CanCommitPlotSelection(AnimationPlotPointerSnapshot snapshot,
        AnimationMember member, Keyframe? key, ulong inputGeneration,
        ulong viewRevision, ulong buildRevision, ulong sourceRevision)
        => !_destroying && IsActiveInHierarchy &&
            _plotInput?.DispatchGeneration == inputGeneration &&
            _plotViewRevision == viewRevision && _plotBuildRevision == buildRevision &&
            _sourceRevision == sourceRevision &&
            ReferenceEquals(snapshot.PanelOwner, this) &&
            ReferenceEquals(snapshot.InputOwner, _plotInput) &&
            ReferenceEquals(CurrentClip(), snapshot.Clip) &&
            ReferenceEquals(_displayedClip, snapshot.Clip) &&
            snapshot.HasCurrentLane(member) &&
            AnimationClipAuthoringActions.ContainsMember(snapshot.Clip, member) &&
            (key is null || MemberContainsKey(member, key));

    private static bool HasCurrentPlotKeyNear(AnimationMember member, float second,
        float tolerance)
    {
        if (member.Animation is not System.Collections.IEnumerable keys)
            return false;
        foreach (object? item in keys)
            if (item is Keyframe key && MathF.Abs(key.Second - second) <= tolerance)
                return true;
        return false;
    }

    internal void PanPlotFromPointer(AnimationPlotPointerSnapshot snapshot,
        Vector2 pressPoint, Vector2 currentPoint)
    {
        if (!IsPlotSourceCurrent(snapshot))
            return;
        if (!ReferenceEquals(_activePlotDrag, snapshot))
        {
            if (snapshot.ViewRevision != _plotViewRevision ||
                !ReferenceEquals(_plotSnapshot, snapshot) ||
                snapshot.BuildRevision != _plotBuildRevision)
                return;
            _activePlotDrag = snapshot;
            _dragViewRevision = _plotViewRevision;
        }
        if (_plotSnapshot is not { } current ||
            !snapshot.CanContinueDrag(_dragViewRevision, _plotViewRevision,
                new Vector2(current.Width, current.Height)))
        {
            _activePlotDrag = null;
            return;
        }
        float nextStart = Math.Clamp(snapshot.Start -
            (currentPoint.X - pressPoint.X) / MathF.Max(1, snapshot.Width - 16) *
            snapshot.Duration, 0, MathF.Max(0, snapshot.ClipLength - snapshot.Duration));
        if (MathF.Abs(nextStart - _plotWindowStart) < 0.0001f)
            return;
        _plotWindowStart = nextStart;
        _plotViewRevision++;
        _dragViewRevision = _plotViewRevision;
        RefreshPlot(snapshot.Clip);
    }

    /// <summary>
    /// Zooms the time axis by one wheel step per unit of <paramref name="wheelDelta"/>.
    /// The time under <paramref name="point"/> in the displayed plot keeps its horizontal
    /// position unless the window reaches a clip edge. Returns true when the snapshot is
    /// current, also at a zoom limit, so the wheel does not scroll an ancestor.
    /// </summary>
    internal bool ZoomPlotAtPointer(AnimationPlotPointerSnapshot snapshot, Vector2 point,
        float wheelDelta)
    {
        if (_activePlotDrag is not null || _activePlotScrub is not null ||
            !IsPlotSourceCurrent(snapshot) ||
            !ReferenceEquals(_plotSnapshot, snapshot) ||
            snapshot.BuildRevision != _plotBuildRevision ||
            snapshot.ViewRevision != _plotViewRevision ||
            !AnimationPlotWheelZoom.TryZoom(_plotZoom, snapshot.Start, snapshot.Duration,
                snapshot.ClipLength, snapshot.Width, point.X, wheelDelta,
                out float zoom, out float start))
            return false;
        if (zoom == _plotZoom && start == _plotWindowStart)
            return true;
        _plotZoom = zoom;
        _plotWindowStart = start;
        _plotViewRevision++;
        RefreshRows();
        return true;
    }

    /// <summary>
    /// Seeks the selected matching component to the frame under a point on the time axis.
    /// A scrub starts only on the current published plot. It continues while the plot view
    /// and size stay the same, also after the seek rebuilds the plot. Returns false when
    /// the snapshot cannot start or continue a scrub.
    /// </summary>
    internal bool ScrubPlotFromPointer(AnimationPlotPointerSnapshot snapshot, Vector2 point)
    {
        if (_activePlotDrag is not null || !IsPlotSourceCurrent(snapshot))
            return false;
        if (!ReferenceEquals(_activePlotScrub, snapshot))
        {
            if (!ReferenceEquals(_plotSnapshot, snapshot) ||
                snapshot.BuildRevision != _plotBuildRevision ||
                snapshot.ViewRevision != _plotViewRevision ||
                !snapshot.IsOnFrameAxis(point))
                return false;
            _activePlotScrub = snapshot;
        }
        if (_plotSnapshot is not { } current ||
            !snapshot.CanContinueDrag(snapshot.ViewRevision, _plotViewRevision,
                new Vector2(current.Width, current.Height)) ||
            SelectedMatchingComponent() is null)
        {
            _activePlotScrub = null;
            return false;
        }
        Seek(snapshot.FrameTimeForX(point.X));
        return true;
    }

    internal void CompletePlotScrub(AnimationPlotPointerSnapshot snapshot)
    {
        if (ReferenceEquals(_activePlotScrub, snapshot))
            _activePlotScrub = null;
    }

    internal void CompletePlotDrag(AnimationPlotPointerSnapshot snapshot)
    {
        if (!ReferenceEquals(_activePlotDrag, snapshot))
            return;
        _activePlotDrag = null;
        if (IsPlotSourceCurrent(snapshot))
            RefreshRows();
    }

    internal void CancelPlotDrag(AnimationPlotPointerSnapshot snapshot)
    {
        if (ReferenceEquals(_activePlotDrag, snapshot))
            _activePlotDrag = null;
        if (ReferenceEquals(_activePlotScrub, snapshot))
            _activePlotScrub = null;
    }
}
