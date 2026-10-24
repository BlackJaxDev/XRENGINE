using System.Numerics;
using XREngine.Animation;

namespace XREngine.Editor;

/// <summary>Frozen plot targets and coordinates for one completed plot build.</summary>
internal sealed partial class AnimationPlotPointerSnapshot
{
    private readonly Lane[] _lanes;
    private readonly KeyHit[] _keys;

    public AnimationPlotPointerSnapshot(object panelOwner, object inputOwner,
        AnimationClip clip, ulong sourceRevision, ulong viewRevision,
        ulong buildRevision, float width, float height, float start, float duration,
        bool graph, Lane[] lanes, KeyHit[] keys)
    {
        PanelOwner = panelOwner;
        InputOwner = inputOwner;
        Clip = clip;
        SourceRevision = sourceRevision;
        ViewRevision = viewRevision;
        BuildRevision = buildRevision;
        Width = width;
        Height = height;
        ClipLength = clip.LengthInSeconds;
        SampleRate = clip.SampleRate;
        Start = start;
        Duration = duration;
        Graph = graph;
        _lanes = [.. lanes];
        _keys = [.. keys];
    }

    public AnimationClip Clip { get; }
    public object PanelOwner { get; }
    public object InputOwner { get; }
    public ulong SourceRevision { get; }
    public ulong ViewRevision { get; }
    public ulong BuildRevision { get; }
    public float Width { get; }
    public float Height { get; }
    public float ClipLength { get; }
    /// <summary>Clip sample rate when the plot was built; frame snapping uses this value.</summary>
    public int SampleRate { get; }
    public float Start { get; }
    public float Duration { get; }
    public bool Graph { get; }

    public bool TryGetLane(Vector2 point, out AnimationMember? member)
    {
        member = null;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
            point.X < 8 || point.X > Width - 8)
            return false;
        foreach (Lane lane in _lanes)
            if (point.Y >= lane.MinY && point.Y < lane.MaxY)
            {
                member = lane.Member;
                return true;
            }
        return false;
    }

    public bool HasCurrentLane(AnimationMember member)
    {
        foreach (Lane lane in _lanes)
            if (ReferenceEquals(lane.Member, member) &&
                ReferenceEquals(lane.Animation, member.Animation))
                return true;
        return false;
    }

    public bool TryHitKey(Vector2 point, out KeyHit hit)
    {
        hit = default;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            return false;
        float best = 81;
        bool found = false;
        foreach (KeyHit candidate in _keys)
        {
            if (!float.IsFinite(candidate.Point.X) ||
                !float.IsFinite(candidate.Point.Y))
                continue;
            float distance = Vector2.DistanceSquared(point, candidate.Point);
            if (!float.IsFinite(distance) || distance > best)
                continue;
            best = distance;
            hit = candidate;
            found = true;
        }
        return found;
    }

    /// <summary>Returns true when the point is in the time axis strip under the lanes.</summary>
    public bool IsOnFrameAxis(Vector2 point)
        => AnimationPlotFrameAxis.IsOnAxis(Width, Height, point.X, point.Y);

    /// <summary>Returns the frame-snapped clip time under a plot-local X position.</summary>
    public float FrameTimeForX(float x)
        => AnimationPlotFrameAxis.SnapToFrame(TimeForX(x), SampleRate, ClipLength);

    public float TimeForX(float x)
        => Math.Clamp(Start + (float.IsFinite(x) ? x - 8 : 0) /
            MathF.Max(1, Width - 16) * Duration,
            0, ClipLength);

    public bool CanContinueDrag(ulong expectedViewRevision,
        ulong currentViewRevision, Vector2 currentSize)
        => expectedViewRevision == currentViewRevision &&
            float.IsFinite(currentSize.X) && float.IsFinite(currentSize.Y) &&
            MathF.Abs(currentSize.X - Width) < 0.5f &&
            MathF.Abs(currentSize.Y - Height) < 0.5f;
}
