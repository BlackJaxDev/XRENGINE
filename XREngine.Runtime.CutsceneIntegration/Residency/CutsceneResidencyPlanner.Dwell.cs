namespace XREngine.Cutscenes;

public sealed partial class CutsceneResidencyPlanner
{
    /// <summary>The default time that a scrub must stay in one place before T1 and T2 work starts: 120 ms.</summary>
    public const double DefaultDwellSeconds = 0.120;

    /// <summary>The default distance that a scrub can move and still count as one place for the dwell gate: 50 ms.</summary>
    public const double DefaultDwellMoveThresholdSeconds = 0.050;

    private bool _scrubbing;
    private bool _dwellOpen;
    private long _dwellAnchor;
    private double _dwellSeconds;
    private double _dwellTimeSeconds = DefaultDwellSeconds;
    private CutsceneTime _dwellMoveThreshold = CutsceneTime.FromSeconds(DefaultDwellMoveThresholdSeconds);

    /// <summary>
    /// The time in seconds that a scrub must stay within <see cref="DwellMoveThreshold"/> before T1 and T2 work starts.
    /// A jump does not use this gate. It must not be negative.
    /// </summary>
    public double DwellSeconds
    {
        get => _dwellTimeSeconds;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _dwellTimeSeconds = value;
        }
    }

    /// <summary>The distance that a scrub can move and still count as one place for the dwell gate. It must not be negative.</summary>
    public CutsceneTime DwellMoveThreshold
    {
        get => _dwellMoveThreshold;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value.Flicks, nameof(value));
            _dwellMoveThreshold = value;
        }
    }

    private void BeginScrub(long position)
    {
        _scrubbing = true;
        _dwellOpen = false;
        _dwellAnchor = position;
        _dwellSeconds = 0.0;
        _dirty = true;
    }

    private void UpdateDwell(long position, double deltaSeconds)
    {
        if (Math.Abs(position - _dwellAnchor) > _dwellMoveThreshold.Flicks)
        {
            _dwellAnchor = position;
            _dwellSeconds = 0.0;
            if (_dwellOpen)
            {
                _dwellOpen = false;
                _dirty = true;
            }
            return;
        }

        if (_dwellOpen)
            return;

        _dwellSeconds += deltaSeconds;
        if (_dwellSeconds >= _dwellTimeSeconds)
        {
            _dwellOpen = true;
            _dirty = true;
        }
    }
}
