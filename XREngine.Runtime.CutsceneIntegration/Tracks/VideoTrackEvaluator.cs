using XREngine.Components;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Shows the clips of video tracks on the <see cref="IVideoFrameTarget"/> component of each bound node, for example a
/// <c>UIVideoComponent</c>, which uploads each frame through its own GPU upload path. Each track keeps one media session
/// and one <see cref="VideoFrameCache"/> for its active clip.
/// </summary>
/// <remarks>
/// <para>
/// The video follows the cutscene time. In forward playback, the decoder runs forward and the cutscene time releases each
/// frame. During a drag of the playhead (a scrub seek, or reverse playback), the track shows the nearest keyframe at or
/// before the time. When the wanted time of a drag stays the same for <see cref="CutsceneVideoChannel.DragDwellSeconds"/>
/// (the editor sends a scrub seek in each frame while the ruler is held), a pause, or a jump, the track decodes the exact
/// frame. A frame in the cache shows with no decode. All decoding runs on the decoder thread of the session; the update
/// thread never waits for it, so a result shows in a later tick.
/// </para>
/// <para>
/// Evaluation only records the wanted time of each track. <see cref="Update"/> does the work, and the player calls it in
/// each tick, also in a tick with no evaluation, so that a seek result shows while the player is paused. A host that does
/// not tick a paused player calls <see cref="CutscenePlayer.UpdateVideo"/>.
/// </para>
/// <para>
/// A file that does not open, a source with no seek (a live stream or an HLS playlist), and a failed seek give one
/// diagnostic each and stop the clip. The track does not fake a seek. No media backend gives the open failure diagnostic.
/// The audio stream of the file is not decoded; cutscene sound uses an <see cref="AudioTrack"/>.
/// </para>
/// </remarks>
public sealed class VideoTrackEvaluator : ICutsceneTrackEvaluator
{
    private readonly List<CutsceneCompileDiagnostic> _diagnostics;
    private readonly string? _assetDirectory;
    private readonly CutsceneVideoChannel?[] _channelOfTrack;
    private readonly CutsceneVideoChannel[] _channels;
    private readonly bool[] _touched;
    private readonly HashSet<(int Track, int Clip, int Code)> _reported = [];
    private readonly CutsceneClipTypeReporter _clipTypes;

    private bool _playingForward;
    private bool _moving;
    private bool _discontinuity;

    private const int CodeNoTarget = 100;
    private const int CodeOverlap = 101;

    /// <summary>Creates an evaluator with one channel for each video track.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="diagnostics">The list that receives the problems that the evaluator finds.</param>
    /// <param name="createSession">
    /// Creates a media session. It runs on a background task. Null uses <see cref="CreateDefaultSession"/>.
    /// </param>
    public VideoTrackEvaluator(CompiledCutscene compiled, List<CutsceneCompileDiagnostic> diagnostics, Func<IMediaStreamSession>? createSession = null)
        : this(compiled, diagnostics, createSession, TimeProvider.System)
    {
    }

    /// <summary>Creates an evaluator with a wall clock for the drag dwell. A test gives a fake clock.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="diagnostics">The list that receives the problems that the evaluator finds.</param>
    /// <param name="createSession">Creates a media session, or null for <see cref="CreateDefaultSession"/>.</param>
    /// <param name="time">The wall clock that measures how long the wanted time of a drag stays the same.</param>
    internal VideoTrackEvaluator(CompiledCutscene compiled, List<CutsceneCompileDiagnostic> diagnostics, Func<IMediaStreamSession>? createSession, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(time);
        _diagnostics = diagnostics;
        _clipTypes = new CutsceneClipTypeReporter(diagnostics);
        _assetDirectory = Path.GetDirectoryName(compiled.Asset.FilePath);

        Func<IMediaStreamSession> create = createSession ?? CreateDefaultSession;
        Action<CutsceneVideoChannel, ECutsceneVideoFailure, string> fail = ReportFailure;
        List<CutsceneTrack> tracks = compiled.Asset.Tracks;
        _channelOfTrack = new CutsceneVideoChannel?[tracks.Count];
        List<CutsceneVideoChannel> channels = [];
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i] is not VideoTrack track)
                continue;
            CutsceneVideoChannel channel = new(i, track, create, fail, time);
            _channelOfTrack[i] = channel;
            channels.Add(channel);
        }
        _channels = [.. channels];
        _touched = new bool[_channels.Length];
    }

    /// <summary>True if the cutscene has at least one video track.</summary>
    public bool HasTracks => _channels.Length > 0;

    /// <summary>
    /// The video residency backend whose frames a track copies on a cache miss, or null. The residency session sets it.
    /// </summary>
    public VideoResidencyBackend? Residency { get; set; }

    /// <summary>The number of seeks that all tracks asked their sessions for.</summary>
    public int SeekRequestCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _channels.Length; i++)
                count += _channels[i].SeekRequestCount;
            return count;
        }
    }

    /// <summary>
    /// Tells the evaluator the transport state before an evaluation. Forward playback decodes sequentially. A move of the
    /// playhead that is not forward playback (a scrub seek or reverse playback) is a drag and shows keyframes. Any other
    /// state decodes the exact frame.
    /// </summary>
    /// <param name="playingForward">True for playback at a positive rate.</param>
    /// <param name="moving">True when the playhead moves by a drag or by reverse playback.</param>
    public void SetTransport(bool playingForward, bool moving)
    {
        _playingForward = playingForward;
        _moving = moving;
    }

    /// <summary>Tells the evaluator that the next evaluation is at a new position, so forward playback must seek again.</summary>
    public void MarkDiscontinuity()
        => _discontinuity = true;

    /// <inheritdoc/>
    public void Evaluate(in CutsceneEvaluationContext context, ReadOnlySpan<CutsceneActiveClip> clips)
    {
        if (_channels.Length == 0)
            return;

        bool discontinuity = _discontinuity;
        _discontinuity = false;
        Array.Clear(_touched);
        ECutsceneVideoMode mode = _playingForward ? ECutsceneVideoMode.Sequential : _moving ? ECutsceneVideoMode.Keyframe : ECutsceneVideoMode.Exact;
        List<CutsceneTrack> tracks = context.Compiled.Asset.Tracks;
        for (int i = 0; i < clips.Length; i++)
        {
            CutsceneActiveClip active = clips[i];
            if ((uint)active.TrackIndex >= (uint)_channelOfTrack.Length || _channelOfTrack[active.TrackIndex] is not { } channel)
                continue;

            CutsceneTrack track = tracks[active.TrackIndex];
            CutsceneClip source = track.Clips[active.ClipIndex];
            if (source is not VideoTrackClip clip)
            {
                _clipTypes.Report(active.TrackIndex, active.ClipIndex, track.Name, nameof(VideoTrackClip), source);
                continue;
            }

            int channelIndex = IndexOf(channel);
            if (_touched[channelIndex])
            {
                if (MarkReported(active.TrackIndex, active.ClipIndex, CodeOverlap))
                    Report(active.TrackIndex, active.ClipIndex, $"The clip {active.ClipIndex} on video track '{track.Name}' overlaps another clip. A video track shows one clip at a time, so the clip is skipped.");
                continue;
            }
            if (HasFailed(active.TrackIndex, active.ClipIndex))
                continue;
            if (!TryBind(context.Bindings, channel, track, active.TrackIndex))
                continue;

            _touched[channelIndex] = true;
            if (channel.ClipIndex != active.ClipIndex)
                channel.Open(active.ClipIndex, AudioTrackEvaluator.ResolveSourcePath(clip.SourcePath, _assetDirectory));
            channel.SetDesired(active.LocalTime.ToSamples((int)TimeSpan.TicksPerSecond), mode, discontinuity);
        }

        // A track with no active clip clears its surface and closes its session.
        for (int i = 0; i < _channels.Length; i++)
            if (!_touched[i] && (_channels[i].ClipIndex >= 0 || _channels[i].Target is not null))
                _channels[i].Release();
    }

    /// <summary>
    /// Services the tracks: collects completed opens and seeks, moves decoded frames to the caches, shows the frame of each
    /// wanted time, and asks for seeks. A drag whose wanted time stays the same for
    /// <see cref="CutsceneVideoChannel.DragDwellSeconds"/> stopped, so the track decodes the exact frame. It never waits for
    /// a decode, and it allocates nothing.
    /// </summary>
    public void Update()
    {
        VideoResidencyBackend? residency = Residency;
        for (int i = 0; i < _channels.Length; i++)
        {
            CutsceneVideoChannel channel = _channels[i];
            if (channel.ClipIndex >= 0)
                channel.Update(residency);
        }
    }

    /// <summary>Clears every surface, closes every session, and forgets the clips.</summary>
    public void Reset()
    {
        for (int i = 0; i < _channels.Length; i++)
            _channels[i].Release();
        Array.Clear(_touched);
        _discontinuity = false;
        _playingForward = false;
        _moving = false;
    }

    /// <summary>
    /// Creates a media session of the registered media backend (<see cref="HlsReferenceRuntime"/>), and starts the backend
    /// the first time. It throws if no backend is registered or the backend does not start.
    /// </summary>
    public static IMediaStreamSession CreateDefaultSession()
    {
        if (HlsReferenceRuntime.Backend is null)
            throw new InvalidOperationException("No media stream decoder is registered. Register the FFmpeg media backend in the application composition root.");
        if (!HlsReferenceRuntime.EnsureStarted())
            throw new InvalidOperationException("The media stream decoder did not start. Check that the FFmpeg libraries are installed.");
        return HlsReferenceRuntime.CreateSession();
    }

    /// <summary>The channel of a track, for tests and diagnostics.</summary>
    /// <param name="trackIndex">The index of the track.</param>
    internal CutsceneVideoChannel? GetChannel(int trackIndex)
        => (uint)trackIndex < (uint)_channelOfTrack.Length ? _channelOfTrack[trackIndex] : null;

    private bool TryBind(CutsceneBindingTable bindings, CutsceneVideoChannel channel, CutsceneTrack track, int trackIndex)
    {
        int slot = track.BindingSlot;
        SceneNode? node = (uint)slot < (uint)bindings.SlotCount && bindings.IsLive(slot) ? bindings.GetNode(slot) as SceneNode : null;
        if (node is not null && channel.Target is XRComponent current && ReferenceEquals(current.SceneNode, node))
            return true;

        IVideoFrameTarget? target = node is null ? null : FindTarget(node);
        if (target is null)
        {
            channel.Release();
            if (MarkReported(trackIndex, -1, CodeNoTarget))
                Report(trackIndex, -1, $"The video track '{track.Name}' has no {nameof(IVideoFrameTarget)} component (for example a UIVideoComponent) on the node of binding slot {slot}. The track shows nothing.");
            return false;
        }

        channel.Bind(target);
        return true;
    }

    private static IVideoFrameTarget? FindTarget(SceneNode node)
    {
        var components = node.Components;
        for (int i = 0; i < components.Count; i++)
            if (components[i] is IVideoFrameTarget target)
                return target;
        return null;
    }

    private int IndexOf(CutsceneVideoChannel channel)
    {
        for (int i = 0; i < _channels.Length; i++)
            if (ReferenceEquals(_channels[i], channel))
                return i;
        return -1;
    }

    private bool HasFailed(int trackIndex, int clipIndex)
        => _reported.Contains((trackIndex, clipIndex, (int)ECutsceneVideoFailure.OpenFailed))
        || _reported.Contains((trackIndex, clipIndex, (int)ECutsceneVideoFailure.CannotSeek))
        || _reported.Contains((trackIndex, clipIndex, (int)ECutsceneVideoFailure.SeekFailed));

    private void ReportFailure(CutsceneVideoChannel channel, ECutsceneVideoFailure failure, string reason)
    {
        int clipIndex = channel.ClipIndex;
        if (!MarkReported(channel.TrackIndex, clipIndex, (int)failure))
            return;

        string what = failure switch
        {
            ECutsceneVideoFailure.OpenFailed => "cannot open its file",
            ECutsceneVideoFailure.CannotSeek => "cannot seek",
            _ => "failed to seek",
        };
        Report(channel.TrackIndex, clipIndex, $"The clip {clipIndex} on video track '{channel.Track.Name}' {what} ('{channel.SourcePath}'): {reason}. The clip shows nothing more.");
    }

    private bool MarkReported(int trackIndex, int clipIndex, int code)
        => _reported.Add((trackIndex, clipIndex, code));

    private void Report(int trackIndex, int clipIndex, string message)
    {
        _diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, clipIndex, message));
        Debug.LogWarning(ELogCategory.Animation, "Cutscene video: {0}", message);
    }
}
