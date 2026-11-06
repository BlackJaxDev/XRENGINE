namespace XREngine.Cutscenes;

public sealed partial class CutsceneResidencyPlanner
{
    /// <summary>The default maximum playback wait at a camera cut: 1 second.</summary>
    public const double DefaultPlaybackWaitMaximumSeconds = 1.0;

    private bool _holding;
    private int _holdBucket = -1;
    private int _releasedCutBucket = -1;
    private double _holdSeconds;
    private int _playbackWaitTimeouts;
    private double _playbackWaitMaximumSeconds = DefaultPlaybackWaitMaximumSeconds;

    /// <summary>
    /// True if forward playback holds at a camera cut until the next shot has its target levels.
    /// The default is false. A jump or a scrub never holds.
    /// </summary>
    public bool PlaybackWaitEnabled { get; set; }

    /// <summary>The maximum time in seconds that one playback wait holds. It must be greater than zero.</summary>
    public double PlaybackWaitMaximumSeconds
    {
        get => _playbackWaitMaximumSeconds;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _playbackWaitMaximumSeconds = value;
        }
    }

    /// <summary>
    /// True if the playback wait holds now. The caller must keep the clock, and the audio with it,
    /// at <see cref="PlaybackHoldTime"/> while this value is true.
    /// </summary>
    public bool IsHoldingPlayback => _holding;

    /// <summary>The time of the camera cut at which the playback wait holds. It is zero when no wait holds.</summary>
    public CutsceneTime PlaybackHoldTime => _holding ? _buckets[_holdBucket].Start : CutsceneTime.Zero;

    /// <summary>The number of playback waits that ended at <see cref="PlaybackWaitMaximumSeconds"/> before the targets were resident.</summary>
    public int PlaybackWaitTimeoutCount => _playbackWaitTimeouts;

    private void UpdatePlaybackWait(long previous, long position, double rate, int previousBucket, int currentBucket, double deltaSeconds)
    {
        if (_holding)
        {
            if (!PlaybackWaitEnabled || rate <= 0.0 || TargetsResident(_holdBucket))
            {
                ReleaseHold();
                return;
            }

            _holdSeconds += deltaSeconds;
            if (_holdSeconds >= _playbackWaitMaximumSeconds)
            {
                _playbackWaitTimeouts++;
                ReleaseHold();
            }
            return;
        }

        if (!PlaybackWaitEnabled || rate <= 0.0 || currentBucket <= previousBucket)
            return;

        // The first cut with missing targets wins when one frame crosses more than one cut.
        for (int b = previousBucket + 1; b <= currentBucket; b++)
        {
            if (!_buckets[b].StartsAtCut || b == _releasedCutBucket)
                continue;

            long cut = _bucketStarts[b];
            if (cut <= previous || cut > position || TargetsResident(b))
                continue;

            _holding = true;
            _holdBucket = b;
            _holdSeconds = 0.0;
            return;
        }
    }

    private void ReleaseHold()
    {
        if (!_holding)
            return;

        _holding = false;
        _releasedCutBucket = _holdBucket;
        _holdBucket = -1;
    }

    private bool TargetsResident(int bucket)
    {
        ResidencyEntry[] entries = _buckets[bucket].Entries;
        for (int e = 0; e < entries.Length; e++)
        {
            ResidencyEntry entry = entries[e];
            if ((int)_backend.GetLevel(_assets[entry.AssetIndex]) < entry.TargetLevel)
                return false;
        }
        return true;
    }
}
