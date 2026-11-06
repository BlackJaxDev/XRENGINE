using System.Diagnostics;
using XREngine.Components;
using XREngine.Data;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Plays the clips of audio tracks through the <see cref="AudioSourceComponent"/> on each bound node.
/// Each track streams its active clip from the file with an <see cref="IStreamingAudioDecoder"/>. The sources play
/// only during forward playback at rate one. A pause, a scrub, and any other rate stop the sources and set their gain to zero.
/// After a jump, the evaluator seeks every active source to the new time.
/// While the clock is in <see cref="ECutsceneClockMode.AudioDisciplined"/> mode, the first active track with
/// <see cref="AudioTrack.DrivesClock"/> gives the position that the listener hears through <see cref="TryReadHeardPosition"/>.
/// The player corrects the clock with it before the evaluation and the event dispatch of a step.
/// If the transport cannot report that position, the clock changes to <see cref="ECutsceneClockMode.Wall"/> and a diagnostic
/// tells why. The clock goes back to audio mode when the position is available again.
/// </summary>
public sealed class AudioTrackEvaluator : ICutsceneTrackEvaluator
{
    /// <summary>The number of frames in each streaming buffer.</summary>
    public const int ChunkFrames = 2048;

    private const int CodeClipType = 0;
    private const int CodeNoComponent = 1;
    private const int CodeOpenFailed = 2;
    private const int CodeChannels = 3;
    private const int CodeSpeed = 4;
    private const int CodeLoop = 5;
    private const int CodeOverlap = 6;
    private const int CodeClock = 7;

    private readonly CutsceneClock _clock;
    private readonly List<CutsceneCompileDiagnostic> _diagnostics;
    private readonly Func<string, IStreamingAudioDecoder> _openDecoder;
    private readonly string? _assetDirectory;
    private readonly CutsceneAudioChannel?[] _channelOfTrack;
    private readonly CutsceneAudioChannel[] _channels;
    private readonly bool[] _touched;
    private readonly HashSet<(int Track, int Clip, int Code)> _reported = [];

    private bool _playing;
    private bool _discontinuity;

    // The track that corrects the clock, from the last evaluation. The player reads it before the next evaluation.
    private CutsceneAudioChannel? _clockChannel;
    private AudioTrackClip? _clockClip;
    private int _clockClipIndex = -1;

    /// <summary>Creates an evaluator with one channel for each audio track.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="clock">The clock that the audio corrects.</param>
    /// <param name="diagnostics">The list that receives the problems that the evaluator finds.</param>
    /// <param name="openDecoder">
    /// Opens a decoder for a full file path. If it is null, <see cref="OpenDefaultDecoder"/> opens Ogg Vorbis and WAV files.
    /// </param>
    /// <param name="getTimestamp">
    /// Gives the time in <see cref="Stopwatch"/> ticks, to measure the time of the audio restarts. If it is null, <see cref="Stopwatch.GetTimestamp"/> is used.
    /// </param>
    public AudioTrackEvaluator(
        CompiledCutscene compiled,
        CutsceneClock clock,
        List<CutsceneCompileDiagnostic> diagnostics,
        Func<string, IStreamingAudioDecoder>? openDecoder = null,
        Func<long>? getTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);
        _clock = clock;
        _diagnostics = diagnostics;
        _openDecoder = openDecoder ?? OpenDefaultDecoder;
        _assetDirectory = Path.GetDirectoryName(compiled.Asset.FilePath);
        Func<long> timestamp = getTimestamp ?? Stopwatch.GetTimestamp;

        List<CutsceneTrack> tracks = compiled.Asset.Tracks;
        _channelOfTrack = new CutsceneAudioChannel?[tracks.Count];
        List<CutsceneAudioChannel> channels = [];
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i] is not AudioTrack track)
                continue;
            CutsceneAudioChannel channel = new(i, track, timestamp);
            _channelOfTrack[i] = channel;
            channels.Add(channel);
        }
        _channels = [.. channels];
        _touched = new bool[_channels.Length];
    }

    /// <summary>True if the cutscene has at least one audio track.</summary>
    public bool HasTracks => _channels.Length > 0;

    /// <summary>True if any audio track can correct the clock.</summary>
    public bool HasClockTrack
    {
        get
        {
            for (int i = 0; i < _channels.Length; i++)
                if (_channels[i].Track.DrivesClock)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// True while the clock uses wall time because the audio transport cannot report a playback position.
    /// The evaluator changed the clock mode to <see cref="ECutsceneClockMode.Wall"/>, and it changes the mode back when the position is available.
    /// </summary>
    public bool IsClockFallbackActive { get; private set; }

    /// <summary>True while the sources are silent because the transport is not in forward playback at rate one.</summary>
    public bool IsMuted => !_playing;

    /// <summary>The output latency in nanoseconds of the last clock reading of the track that corrects the clock, or zero.</summary>
    public long ClockLatencyNanoseconds => _clockChannel?.LastLatencyNanoseconds ?? 0L;

    /// <summary>
    /// Tells the evaluator the transport state before an evaluation. Sources play only when <paramref name="playing"/>
    /// is true and the rate is one. A change to any other state stops the sources at once, also with no evaluation.
    /// </summary>
    /// <param name="playing">True if the player is in forward playback.</param>
    /// <param name="rate">The playback rate.</param>
    public void SetTransport(bool playing, double rate)
    {
        bool audible = playing && Math.Abs(rate - 1.0) < 1e-9;
        if (_playing && !audible)
            for (int i = 0; i < _channels.Length; i++)
                _channels[i].Silence();
        _playing = audible;
    }

    /// <summary>
    /// Returns the wall time in seconds that the restarts of all tracks took since the last call, and sets it to zero.
    /// A restart includes the decoder seek, which can take a long time. The sources start to play at its end,
    /// so the player removes this time from the next clock advance.
    /// </summary>
    public double ConsumeRestartSeconds()
    {
        double seconds = 0.0;
        for (int i = 0; i < _channels.Length; i++)
            seconds += _channels[i].ConsumeRestartSeconds();
        return seconds;
    }

    /// <summary>Tells the evaluator that the next evaluation is at a new position, so every active source must seek.</summary>
    public void MarkDiscontinuity()
        => _discontinuity = true;

    /// <inheritdoc/>
    public void Evaluate(in CutsceneEvaluationContext context, ReadOnlySpan<CutsceneActiveClip> clips)
    {
        if (_channels.Length == 0)
            return;

        bool restart = _discontinuity;
        _discontinuity = false;
        Array.Clear(_touched);

        CutsceneAudioChannel? clockChannel = null;
        AudioTrackClip? clockClip = null;
        int clockClipIndex = -1;
        List<CutsceneTrack> tracks = context.Compiled.Asset.Tracks;
        for (int i = 0; i < clips.Length; i++)
        {
            CutsceneActiveClip active = clips[i];
            if ((uint)active.TrackIndex >= (uint)_channelOfTrack.Length || _channelOfTrack[active.TrackIndex] is not { } channel)
                continue;

            CutsceneTrack track = tracks[active.TrackIndex];
            CutsceneClip source = track.Clips[active.ClipIndex];
            if (source is not AudioTrackClip clip)
            {
                if (MarkReported(active.TrackIndex, active.ClipIndex, CodeClipType))
                    Report(active.TrackIndex, active.ClipIndex, $"The clip {active.ClipIndex} on track '{track.Name}' is a {source.GetType().Name}. The track needs an {nameof(AudioTrackClip)}. The clip is skipped.");
                continue;
            }

            // A hold clip stays active after its duration, but an audio clip has no sound there.
            if (context.Time >= clip.Start + clip.Duration)
                continue;

            int channelIndex = IndexOf(channel);
            if (_touched[channelIndex])
            {
                if (MarkReported(active.TrackIndex, active.ClipIndex, CodeOverlap))
                    Report(active.TrackIndex, active.ClipIndex, $"The clip {active.ClipIndex} on audio track '{track.Name}' overlaps another clip. An audio track plays one clip at a time, so the clip is skipped.");
                continue;
            }

            if (!CanPlay(active, track, clip) || HasOpenFailure(active.TrackIndex, active.ClipIndex))
                continue;
            if (!TryBind(context.Bindings, channel, track, active.TrackIndex))
                continue;
            if (channel.ClipIndex != active.ClipIndex && !TryOpen(channel, track, clip, active))
                continue;

            _touched[channelIndex] = true;
            if (!_playing)
            {
                channel.Silence();
                continue;
            }

            float gain = channel.Component!.Gain * clip.Volume * active.Weight;
            channel.Service(active.LocalTime.ToSamples(channel.SampleRate), restart, gain);
            if (clockChannel is null && channel.Track.DrivesClock)
            {
                clockChannel = channel;
                clockClip = clip;
                clockClipIndex = active.ClipIndex;
            }
        }

        // A track with no playing clip stops and gives its component back.
        for (int i = 0; i < _channels.Length; i++)
            if (!_touched[i] && (_channels[i].ClipIndex >= 0 || _channels[i].Component is not null))
                _channels[i].Release();

        _clockChannel = clockChannel;
        _clockClip = clockClip;
        _clockClipIndex = clockClipIndex;
    }

    /// <summary>Stops every source, closes every decoder, and gives each component back with its earlier settings.</summary>
    public void Reset()
    {
        for (int i = 0; i < _channels.Length; i++)
            _channels[i].Release();
        Array.Clear(_touched);
        _discontinuity = false;
        _playing = false;
        _clockChannel = null;
        _clockClip = null;
        _clockClipIndex = -1;
        if (IsClockFallbackActive)
        {
            IsClockFallbackActive = false;
            _clock.Mode = ECutsceneClockMode.AudioDisciplined;
        }
    }

    /// <summary>Opens an Ogg Vorbis or WAV file by its extension.</summary>
    /// <param name="path">The full path of the file.</param>
    /// <exception cref="NotSupportedException">The file has another extension.</exception>
    public static IStreamingAudioDecoder OpenDefaultDecoder(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            return new VorbisStreamingDecoder(path);
        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return new WavStreamingDecoder(path);
        throw new NotSupportedException($"No streaming decoder supports the extension '{extension}'. Use an Ogg Vorbis or WAV file.");
    }

    /// <summary>
    /// Reads the cutscene time that the listener hears now, from the track that corrects the clock in the last evaluation.
    /// The player calls this after it advances the clock and before it evaluates, so the correction and the events of a step use one position.
    /// If the transport cannot report a clock, this changes the clock to <see cref="ECutsceneClockMode.Wall"/> and adds one diagnostic.
    /// When the clock is available again, the mode goes back to <see cref="ECutsceneClockMode.AudioDisciplined"/>.
    /// </summary>
    /// <param name="deltaSeconds">
    /// The wall time of this play step. A warm-up after a restart that lasts longer than the output latency plus 250 ms
    /// (at least 500 ms) of play steps counts as a lost clock.
    /// </param>
    /// <param name="position">The heard cutscene time.</param>
    /// <param name="firstAfterRestart">
    /// True if this is the first valid position after the sources restarted. The clock can then move to it in one step.
    /// </param>
    /// <returns>False if no track can give a position now, or if the clock is not in audio mode.</returns>
    public bool TryReadHeardPosition(double deltaSeconds, out CutsceneTime position, out bool firstAfterRestart)
    {
        position = default;
        firstAfterRestart = false;
        if (!_playing || _clockChannel is not { } channel || _clockClip is not { } clip)
            return false;
        if (_clock.Mode != ECutsceneClockMode.AudioDisciplined && !IsClockFallbackActive)
            return false;

        // A channel with no listener, or a channel that closed or changed its clip, has nothing to measure.
        if (!channel.IsStreaming || channel.ClipIndex != _clockClipIndex)
            return false;

        if (!channel.TryReadHeardFrame(deltaSeconds, out long frame, out bool warmingUp, out firstAfterRestart))
        {
            // A new queue that the listener does not hear yet is expected after each restart. The clock stays on wall time
            // for this step, and this is not a loss of the clock.
            if (warmingUp || IsClockFallbackActive)
                return false;
            IsClockFallbackActive = true;
            _clock.Mode = ECutsceneClockMode.Wall;
            string message = $"The audio transport of track '{channel.Track.Name}' cannot report a playback clock, or its clock does not reach the queued audio in time. The cutscene clock uses wall time until the clock is available.";
            if (MarkReported(channel.TrackIndex, -1, CodeClock))
                Report(channel.TrackIndex, -1, message);
            else
                Debug.LogWarning(ELogCategory.Audio, "Cutscene audio: {0}", message);
            return false;
        }

        if (IsClockFallbackActive)
        {
            IsClockFallbackActive = false;
            _clock.Mode = ECutsceneClockMode.AudioDisciplined;
            Debug.Log(ELogCategory.Audio, "Cutscene audio: the playback clock of track '{0}' is available again. The cutscene clock follows the audio.", channel.Track.Name);
        }

        // The clip speed is one, so the heard time on the timeline is the clip start plus the file time after the clip offset.
        CutsceneTime fileTime = CutsceneTime.FromSamples(frame, channel.SampleRate);
        position = clip.Start + (fileTime - clip.ClipOffset);
        return true;
    }
    private bool CanPlay(in CutsceneActiveClip active, CutsceneTrack track, AudioTrackClip clip)
    {
        if (Math.Abs(clip.Speed - 1.0) > 1e-9)
        {
            if (MarkReported(active.TrackIndex, active.ClipIndex, CodeSpeed))
                Report(active.TrackIndex, active.ClipIndex, $"The clip {active.ClipIndex} on audio track '{track.Name}' has the speed {clip.Speed}. An audio clip must have the speed 1, so the clip is skipped.");
            return false;
        }
        if (clip.Extrapolation == ECutsceneClipExtrapolation.Loop)
        {
            if (MarkReported(active.TrackIndex, active.ClipIndex, CodeLoop))
                Report(active.TrackIndex, active.ClipIndex, $"The clip {active.ClipIndex} on audio track '{track.Name}' loops. An audio clip cannot loop, so the clip is skipped.");
            return false;
        }
        return true;
    }

    private bool TryBind(CutsceneBindingTable bindings, CutsceneAudioChannel channel, CutsceneTrack track, int trackIndex)
    {
        int slot = track.BindingSlot;
        AudioSourceComponent? component = null;
        if ((uint)slot < (uint)bindings.SlotCount && bindings.IsLive(slot) && bindings.GetNode(slot) is SceneNode node)
            node.TryGetComponent(out component);

        if (component is null)
        {
            channel.Release();
            if (MarkReported(trackIndex, -1, CodeNoComponent))
                Report(trackIndex, -1, $"The audio track '{track.Name}' has no {nameof(AudioSourceComponent)} on the node of binding slot {slot}. The track plays nothing.");
            return false;
        }

        channel.Bind(component);
        return true;
    }

    private bool TryOpen(CutsceneAudioChannel channel, CutsceneTrack track, AudioTrackClip clip, in CutsceneActiveClip active)
    {
        IStreamingAudioDecoder decoder;
        try
        {
            decoder = _openDecoder(ResolvePath(clip.SourcePath));
        }
        catch (Exception error)
        {
            channel.Deactivate();
            if (MarkReported(active.TrackIndex, active.ClipIndex, CodeOpenFailed))
                Report(active.TrackIndex, active.ClipIndex, $"The clip {active.ClipIndex} on audio track '{track.Name}' cannot open '{clip.SourcePath}': {error.Message} The clip is skipped.");
            return false;
        }

        if (decoder.Channels is < 1 or > 2)
        {
            int channels = decoder.Channels;
            decoder.Dispose();
            channel.Deactivate();
            if (MarkReported(active.TrackIndex, active.ClipIndex, CodeChannels))
                Report(active.TrackIndex, active.ClipIndex, $"The file '{clip.SourcePath}' of clip {active.ClipIndex} on audio track '{track.Name}' has {channels} channels. An audio clip must be mono or stereo, so the clip is skipped.");
            return false;
        }

        channel.Open(active.ClipIndex, clip, decoder);
        return true;
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path) || string.IsNullOrEmpty(_assetDirectory))
            return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(_assetDirectory, path));
    }

    private int IndexOf(CutsceneAudioChannel channel)
    {
        for (int i = 0; i < _channels.Length; i++)
            if (ReferenceEquals(_channels[i], channel))
                return i;
        return -1;
    }

    // A clip whose file failed to open is not opened again in each frame. A new player tries again.
    private bool HasOpenFailure(int trackIndex, int clipIndex)
        => _reported.Contains((trackIndex, clipIndex, CodeOpenFailed)) || _reported.Contains((trackIndex, clipIndex, CodeChannels));

    private bool MarkReported(int trackIndex, int clipIndex, int code)
        => _reported.Add((trackIndex, clipIndex, code));

    private void Report(int trackIndex, int clipIndex, string message)
    {
        _diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, clipIndex, message));
        Debug.LogWarning(ELogCategory.Audio, "Cutscene audio: {0}", message);
    }
}
