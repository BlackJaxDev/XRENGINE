namespace XREngine.Cutscenes;

/// <summary>
/// Fires the events of the event tracks of a cutscene. The dispatcher sorts all events by time when it is created.
/// A dispatch does a binary search and does not allocate. A dispatch that crosses no event costs one search.
/// Events are the only side effect of a cutscene. They never change state that an evaluation needs.
/// </summary>
public sealed class CutsceneEventDispatcher
{
    private readonly long[] _times;
    private readonly int[] _trackIndices;
    private readonly int[] _eventIndices;
    private readonly CutsceneEvent[] _events;
    private readonly EventTrack[] _tracks;
    private readonly List<CutsceneCompileDiagnostic> _diagnostics;
    private readonly HashSet<int> _reportedTracks = [];

    /// <summary>Creates a dispatcher for the event tracks of an asset.</summary>
    /// <param name="asset">The cutscene asset.</param>
    /// <param name="diagnostics">The list that receives the problems that the dispatcher finds.</param>
    public CutsceneEventDispatcher(CutsceneAsset asset, List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(diagnostics);
        _diagnostics = diagnostics;

        List<(long Time, int Track, int Event)> entries = [];
        for (int trackIndex = 0; trackIndex < asset.Tracks.Count; trackIndex++)
        {
            if (asset.Tracks[trackIndex] is not EventTrack track)
                continue;

            if (track.Clips.Count > 0)
                diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, -1, $"The event track '{track.Name}' has clips. An event track uses only its events, so the clips have no effect."));

            for (int eventIndex = 0; eventIndex < track.Events.Count; eventIndex++)
            {
                if (track.Events[eventIndex] is { } cutsceneEvent)
                    entries.Add((cutsceneEvent.Time.Flicks, trackIndex, eventIndex));
            }
        }

        // Time order first. Events at the same time keep the order of their tracks and of their lists.
        entries.Sort();

        _times = new long[entries.Count];
        _trackIndices = new int[entries.Count];
        _eventIndices = new int[entries.Count];
        _events = new CutsceneEvent[entries.Count];
        _tracks = new EventTrack[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            (long time, int trackIndex, int eventIndex) = entries[i];
            EventTrack track = (EventTrack)asset.Tracks[trackIndex];
            _times[i] = time;
            _trackIndices[i] = trackIndex;
            _eventIndices[i] = eventIndex;
            _events[i] = track.Events[eventIndex];
            _tracks[i] = track;
        }
    }

    /// <summary>Raised for each event that fires.</summary>
    public event Action<CutsceneFiredEvent>? Fired;

    /// <summary>
    /// True for an editor preview. A preview fires only the tracks that have <see cref="EventTrack.FireInPreview"/> set.
    /// </summary>
    public bool IsPreview { get; set; }

    /// <summary>The number of events of all event tracks.</summary>
    public int EventCount => _times.Length;

    /// <summary>Fires each event that forward playback crossed, in time order. A range that does not go forward fires nothing.</summary>
    /// <param name="from">The position before the step.</param>
    /// <param name="to">The position after the step.</param>
    /// <param name="includeFrom">True if an event at <paramref name="from"/> has not fired yet.</param>
    public void DispatchPlay(CutsceneTime from, CutsceneTime to, bool includeFrom)
    {
        if (_times.Length == 0 || to <= from)
            return;

        int end = FindFirstAfter(to.Flicks);
        for (int i = FindFirst(from.Flicks, includeFrom); i < end; i++)
        {
            if (IsPreview && !_tracks[i].FireInPreview)
                continue;
            Fire(i, fromSeek: false);
        }
    }

    /// <summary>
    /// Fires the events that a forward seek crossed, by the seek policy of each track. A backward seek fires nothing.
    /// A scrub fires only the tracks that have <see cref="EventTrack.FireDuringScrub"/> set.
    /// </summary>
    /// <param name="from">The position before the seek.</param>
    /// <param name="to">The target of the seek.</param>
    /// <param name="includeFrom">True if an event at <paramref name="from"/> has not fired yet.</param>
    /// <param name="scrub">True if the seek is a step of a scrub.</param>
    public void DispatchSeek(CutsceneTime from, CutsceneTime to, bool includeFrom, bool scrub)
    {
        if (_times.Length == 0 || to <= from)
            return;

        int end = FindFirstAfter(to.Flicks);
        for (int i = FindFirst(from.Flicks, includeFrom); i < end; i++)
        {
            EventTrack track = _tracks[i];
            if ((IsPreview && !track.FireInPreview) || (scrub && !track.FireDuringScrub))
                continue;

            switch (track.SeekPolicy)
            {
                case ECutsceneEventSeekPolicy.FireAll:
                    Fire(i, fromSeek: true);
                    break;
                case ECutsceneEventSeekPolicy.FireLatest:
                    if (IsLastOfKey(i, end))
                        Fire(i, fromSeek: true);
                    break;
            }
        }
    }

    // A later crossed event of the same track and key replaces this one. The scan is quadratic in the crossed
    // events, but it does not allocate, and a seek crosses few events.
    private bool IsLastOfKey(int index, int end)
    {
        int trackIndex = _trackIndices[index];
        string key = _events[index].Key;
        for (int j = index + 1; j < end; j++)
        {
            if (_trackIndices[j] == trackIndex && string.Equals(_events[j].Key, key, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private void Fire(int index, bool fromSeek)
    {
        Action<CutsceneFiredEvent>? handler = Fired;
        if (handler is null)
            return;

        // A failed handler must not stop the cutscene or the other events of the frame.
        try
        {
            handler(new CutsceneFiredEvent(_trackIndices[index], _eventIndices[index], _events[index], fromSeek));
        }
        catch (Exception exception)
        {
            ReportHandlerFailure(index, exception);
        }
    }

    private void ReportHandlerFailure(int index, Exception exception)
    {
        int trackIndex = _trackIndices[index];
        string message = $"A handler of the event '{_events[index].Key}' on track '{_tracks[index].Name}' failed. {exception.GetBaseException().Message}";
        Debug.LogWarning(ELogCategory.Animation, "Cutscene event: {0}", message);
        if (_reportedTracks.Add(trackIndex))
            _diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, _eventIndices[index], message));
    }

    // The first index whose time is after the start, or at the start if the start is inclusive.
    private int FindFirst(long start, bool inclusive)
        => inclusive ? FindFirstAtOrAfter(start) : FindFirstAfter(start);

    private int FindFirstAfter(long time)
    {
        int low = 0;
        int high = _times.Length;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (_times[mid] <= time)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    private int FindFirstAtOrAfter(long time)
    {
        int low = 0;
        int high = _times.Length;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (_times[mid] < time)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }
}
