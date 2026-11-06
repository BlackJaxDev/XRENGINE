using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Data;
using XREngine.Scene;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class AudioTrackTests
{
    private const int SampleRate = 8000;

    private sealed class FakeResolver(params SceneNode[] nodes) : ICutsceneBindingResolver
    {
        public SceneNode? FindNodeById(Guid id)
        {
            foreach (SceneNode node in nodes)
                if (node.ID == id)
                    return node;
            return null;
        }

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    /// <summary>The state of one source in the fake transport.</summary>
    private sealed class FakeSource
    {
        public readonly List<uint> Queue = [];
        public readonly List<string> Log = [];
        public int Processed;
        public bool Playing;
        public float Gain = 1.0f;
        public long SampleFrame;
    }

    /// <summary>
    /// A transport that records the commands of each source. Stop marks every queued buffer as processed, as OpenAL does.
    /// The log of a source holds "stop", "play", and "queue N", where N is the first sample of the queued buffer.
    /// </summary>
    private sealed class FakeTransport : IAudioTransport
    {
        private readonly Dictionary<uint, short> _firstSample = [];
        private uint _nextSource = 1;
        private uint _nextBuffer = 1;

        public Dictionary<uint, FakeSource> Sources { get; } = [];
        public bool ClockAvailable { get; set; } = true;
        public bool Recording { get; set; } = true;
        public long LatencyNanoseconds { get; set; }

        public string? DeviceName => null;
        public int SampleRate => 48000;
        public bool IsOpen => true;

        public void Open(string? deviceName = null) { }
        public void Close() { }
        public void Dispose() { }

        public void SetListenerPosition(Vector3 position) { }
        public void SetListenerVelocity(Vector3 velocity) { }
        public void SetListenerOrientation(Vector3 forward, Vector3 up) { }
        public void SetListenerGain(float gain) { }

        public AudioSourceHandle CreateSource()
        {
            uint id = _nextSource++;
            Sources[id] = new FakeSource();
            return new AudioSourceHandle(id);
        }

        public void DestroySource(AudioSourceHandle source) => Sources.Remove(source.Id);

        public AudioBufferHandle CreateBuffer() => new(_nextBuffer++);

        public void DestroyBuffer(AudioBufferHandle buffer) => _firstSample.Remove(buffer.Id);

        public void UploadBufferData(AudioBufferHandle buffer, ReadOnlySpan<byte> pcm, int frequency, int channels, SampleFormat format)
            => _firstSample[buffer.Id] = pcm.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(pcm) : (short)0;

        public void Play(AudioSourceHandle source)
        {
            FakeSource s = Sources[source.Id];
            s.Playing = true;
            if (Recording)
                s.Log.Add("play");
        }

        public void Stop(AudioSourceHandle source)
        {
            FakeSource s = Sources[source.Id];
            s.Playing = false;
            s.Processed = s.Queue.Count;
            if (Recording)
                s.Log.Add("stop");
        }

        public void Pause(AudioSourceHandle source) => Sources[source.Id].Playing = false;

        public void Rewind(AudioSourceHandle source) => Sources[source.Id].Playing = false;

        public void SetSourceBuffer(AudioSourceHandle source, AudioBufferHandle buffer) { }

        public void QueueBuffers(AudioSourceHandle source, ReadOnlySpan<AudioBufferHandle> buffers)
        {
            FakeSource s = Sources[source.Id];
            foreach (AudioBufferHandle buffer in buffers)
            {
                s.Queue.Add(buffer.Id);
                if (Recording)
                    s.Log.Add($"queue {_firstSample[buffer.Id]}");
            }
        }

        public int UnqueueProcessedBuffers(AudioSourceHandle source, Span<AudioBufferHandle> output)
        {
            FakeSource s = Sources[source.Id];
            int count = Math.Min(Math.Min(s.Processed, output.Length), s.Queue.Count);
            for (int i = 0; i < count; i++)
                output[i] = new AudioBufferHandle(s.Queue[i]);
            s.Queue.RemoveRange(0, count);
            s.Processed -= count;
            return count;
        }

        public int GetBuffersProcessed(AudioSourceHandle source) => Sources[source.Id].Processed;

        public int GetBuffersQueued(AudioSourceHandle source) => Sources[source.Id].Queue.Count;

        public void SetSourcePosition(AudioSourceHandle source, Vector3 position) { }
        public void SetSourceVelocity(AudioSourceHandle source, Vector3 velocity) { }
        public void SetSourceGain(AudioSourceHandle source, float gain) => Sources[source.Id].Gain = gain;
        public void SetSourcePitch(AudioSourceHandle source, float pitch) { }
        public void SetSourceLooping(AudioSourceHandle source, bool loop) { }
        public bool IsSourcePlaying(AudioSourceHandle source) => Sources[source.Id].Playing;
        public int GetSampleOffset(AudioSourceHandle source) => (int)Sources[source.Id].SampleFrame;

        public bool TryGetPlaybackClock(AudioSourceHandle source, out long sampleFrame, out long latencyNanoseconds)
        {
            sampleFrame = 0;
            latencyNanoseconds = 0;
            if (!ClockAvailable)
                return false;
            sampleFrame = Sources[source.Id].SampleFrame;
            latencyNanoseconds = LatencyNanoseconds;
            return true;
        }

        public IAudioCaptureDevice? OpenCaptureDevice(string? device, int sampleRate, SampleFormat format, int bufferSize) => null;

        public void ClearLogs()
        {
            foreach (FakeSource source in Sources.Values)
                source.Log.Clear();
        }

        public void SetSampleFrame(long frame)
        {
            foreach (FakeSource source in Sources.Values)
                source.SampleFrame = frame;
        }

        /// <summary>Marks the first queued buffer of each source as played, as the device does.</summary>
        public void ConsumeOneBuffer()
        {
            foreach (FakeSource source in Sources.Values)
                source.Processed = Math.Min(source.Queue.Count, source.Processed + 1);
        }
    }

    /// <summary>A decoder that wraps another decoder and counts the seeks that go backward from its read position.</summary>
    private sealed class CountingDecoder(IStreamingAudioDecoder inner) : IStreamingAudioDecoder
    {
        private long _position;

        public int BackwardSeeks { get; private set; }
        public int Seeks { get; private set; }
        public int SampleRate => inner.SampleRate;
        public int Channels => inner.Channels;
        public long TotalFrames => inner.TotalFrames;

        public void SeekToFrame(long frame)
        {
            Seeks++;
            if (frame < _position)
                BackwardSeeks++;
            inner.SeekToFrame(frame);
            _position = Math.Clamp(frame, 0L, TotalFrames);
        }

        public int Read(Span<float> destination)
        {
            int read = inner.Read(destination);
            _position += read;
            return read;
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>A cutscene with one audio clip on a node with two listeners, each with its own fake transport.</summary>
    private sealed class Rig : IDisposable
    {
        public required string Path { get; init; }
        public required CutscenePlayer Player { get; init; }
        public required AudioSourceComponent Component { get; init; }
        public required FakeTransport[] Transports { get; init; }
        public required ListenerContext[] Listeners { get; init; }
        public required CutsceneBindingTable Table { get; init; }
        public required List<CountingDecoder> Decoders { get; init; }
        public required List<string> Fired { get; init; }
        public required List<CutsceneDiscontinuity> Discontinuities { get; init; }

        public void SetHeardTime(double seconds, long restartFrame)
        {
            foreach (FakeTransport transport in Transports)
                transport.SetSampleFrame((long)Math.Round(seconds * SampleRate) - restartFrame);
        }

        public IEnumerable<FakeSource> AllSources
        {
            get
            {
                foreach (FakeTransport transport in Transports)
                    foreach (FakeSource source in transport.Sources.Values)
                        yield return source;
            }
        }

        public void SetClockAvailable(bool available)
        {
            foreach (FakeTransport transport in Transports)
                transport.ClockAvailable = available;
        }

        public void Dispose()
        {
            Player.Stop();
            Table.Dispose();
            foreach (ListenerContext listener in Listeners)
                listener.Dispose();
            File.Delete(Path);
        }
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    /// <summary>Writes a mono 16-bit WAV file in which the value of each sample is its frame index.</summary>
    private static string WriteRampWav(int frames)
    {
        string path = Path.Combine(Path.GetTempPath(), $"xre-cutscene-audio-{Guid.NewGuid():N}.wav");
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream, Encoding.ASCII);
        int dataLength = frames * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        for (int f = 0; f < frames; f++)
            writer.Write((short)f);
        return path;
    }

    private static Rig MakeRig(ECutsceneEventSeekPolicy policy = ECutsceneEventSeekPolicy.Skip, params double[] eventTimes)
    {
        string path = WriteRampWav(SampleRate * 3);
        SceneNode node = new("Speaker");
        AudioSourceComponent component = node.AddComponent<AudioSourceComponent>()!;

        FakeTransport[] transports = [new(), new()];
        ListenerContext[] listeners = [new(transports[0], null), new(transports[1], null)];
        foreach (ListenerContext listener in listeners)
            component.AddSourceToListener(listener);

        CutsceneAsset asset = new() { Duration = T(3) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
        AudioTrack track = new() { Name = "Dialog", BindingSlot = 0 };
        track.Clips.Add(new AudioTrackClip { Start = T(0), Duration = T(3), SourcePath = path });
        asset.Tracks.Add(track);
        if (eventTimes.Length > 0)
        {
            EventTrack events = new() { Name = "Events", SeekPolicy = policy };
            foreach (double time in eventTimes)
                events.Events.Add(new CutsceneEvent { Time = T(time), Key = "e" });
            asset.Tracks.Add(events);
        }

        List<CutsceneCompileDiagnostic> diagnostics = [];
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, diagnostics);
        diagnostics.ShouldBeEmpty();
        CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
        table.Diagnostics.ShouldBeEmpty();
        List<CountingDecoder> decoders = [];
        CutscenePlayer player = new(compiled, table, new CutsceneStateSnapshot(table), file =>
        {
            CountingDecoder decoder = new(AudioTrackEvaluator.OpenDefaultDecoder(file));
            decoders.Add(decoder);
            return decoder;
        });
        List<string> fired = [];
        List<CutsceneDiscontinuity> discontinuities = [];
        player.EventFired += e => fired.Add($"{e.Event.Time.ToSeconds():0.###}{(e.FromSeek ? " seek" : "")}");
        player.Discontinuity += discontinuities.Add;

        foreach (FakeTransport transport in transports)
            transport.ClearLogs();
        return new Rig
        {
            Path = path,
            Player = player,
            Component = component,
            Transports = transports,
            Listeners = listeners,
            Table = table,
            Decoders = decoders,
            Fired = fired,
            Discontinuities = discontinuities,
        };
    }

    private static int FirstQueuedSample(FakeSource source)
    {
        foreach (string entry in source.Log)
            if (entry.StartsWith("queue ", StringComparison.Ordinal))
                return int.Parse(entry["queue ".Length..]);
        throw new ShouldAssertException("The source queued no buffer.");
    }

    [Test]
    public void Play_StreamsTheClipFromTheTimeIntoEverySource()
    {
        using Rig rig = MakeRig();
        rig.Player.Clock.Mode.ShouldBe(ECutsceneClockMode.AudioDisciplined);

        rig.Player.Seek(T(0.25), ESeekIntent.Jump);
        rig.Player.Play();
        rig.Player.Tick(0.0);

        rig.Player.Diagnostics.ShouldBeEmpty();
        rig.Component.ExternalBufferManagement.ShouldBeTrue();
        foreach (FakeSource source in rig.AllSources)
        {
            source.Playing.ShouldBeTrue();
            source.Queue.Count.ShouldBe(4);
            FirstQueuedSample(source).ShouldBe(2000);
            source.Log[^1].ShouldBe("play");
        }
    }

    [Test]
    public void Discontinuity_SeeksEveryActiveSource()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.0);
        foreach (FakeSource source in rig.AllSources)
            FirstQueuedSample(source).ShouldBe(0);

        foreach (FakeTransport transport in rig.Transports)
            transport.ClearLogs();
        rig.Player.Seek(T(1.5), ESeekIntent.Jump);
        rig.Player.Tick(0.0);

        int count = 0;
        foreach (FakeSource source in rig.AllSources)
        {
            count++;
            source.Log[0].ShouldBe("stop");
            FirstQueuedSample(source).ShouldBe(12000);
            source.Log[^1].ShouldBe("play");
            source.Playing.ShouldBeTrue();
            source.Queue.Count.ShouldBe(4);
        }
        count.ShouldBe(2);
    }

    [Test]
    public void Clock_FollowsTheHeardPositionWithTheLatency()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);

        // The queue starts at frame 160. The device is 4000 frames into it, and 50 ms (400 frames) of that is not heard yet.
        foreach (FakeTransport transport in rig.Transports)
        {
            transport.SetSampleFrame(4000);
            transport.LatencyNanoseconds = 50_000_000;
        }
        rig.Player.Tick(0.02);

        // The heard frame is 3760, which is 0.47 s. The difference is above the hard resync limit, so the clock jumps.
        rig.Player.Position.ShouldBe(CutsceneTime.FromSamples(160 + 4000 - 400, SampleRate));
        rig.Player.Clock.Mode.ShouldBe(ECutsceneClockMode.AudioDisciplined);
        rig.Player.IsAudioClockFallback.ShouldBeFalse();
    }

    [Test]
    public void Clock_WithoutATransportClock_UsesWallTimeAndReportsIt()
    {
        using Rig rig = MakeRig();
        rig.SetClockAvailable(false);
        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.Player.Tick(0.02);

        rig.Player.Clock.Mode.ShouldBe(ECutsceneClockMode.Wall);
        rig.Player.IsAudioClockFallback.ShouldBeTrue();
        rig.Player.Diagnostics.ShouldContain(d => d.Message.Contains("cannot report a playback clock"));
        rig.Player.Position.ShouldBe(T(0.04));

        // Wall time moves the clock on, and the diagnostic is added once.
        rig.Player.Tick(0.02);
        rig.Player.Position.ShouldBe(T(0.06));
        rig.Player.Diagnostics.Count.ShouldBe(1);

        // The clock goes back to audio mode when the transport reports a clock again.
        rig.SetClockAvailable(true);
        foreach (FakeTransport transport in rig.Transports)
            transport.SetSampleFrame(0);
        rig.Player.Tick(0.02);
        rig.Player.Clock.Mode.ShouldBe(ECutsceneClockMode.AudioDisciplined);
        rig.Player.IsAudioClockFallback.ShouldBeFalse();
    }

    [Test]
    public void SetRate_OtherThanOne_MutesTheSources()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);
        foreach (FakeSource source in rig.AllSources)
        {
            source.Playing.ShouldBeTrue();
            source.Gain.ShouldBe(1.0f);
        }

        rig.Player.SetRate(2.0);
        rig.Player.Tick(0.02);

        rig.Player.IsAudioMuted.ShouldBeTrue();
        foreach (FakeSource source in rig.AllSources)
        {
            source.Playing.ShouldBeFalse();
            source.Gain.ShouldBe(0.0f);
        }

        // Rate one plays again from the current time.
        rig.Player.SetRate(1.0);
        foreach (FakeTransport transport in rig.Transports)
            transport.ClearLogs();
        rig.Player.Tick(0.02);

        rig.Player.IsAudioMuted.ShouldBeFalse();
        int frame = (int)rig.Player.Position.ToSamples(SampleRate);
        foreach (FakeSource source in rig.AllSources)
        {
            source.Playing.ShouldBeTrue();
            source.Gain.ShouldBe(1.0f);
            FirstQueuedSample(source).ShouldBe(frame);
        }
    }

    [Test]
    public void Pause_AndStop_SilenceTheSourcesAndGiveTheComponentBack()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);

        rig.Player.Pause();
        rig.Player.Tick(0.02);
        foreach (FakeSource source in rig.AllSources)
            source.Playing.ShouldBeFalse();

        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.Player.Stop();

        rig.Component.ExternalBufferManagement.ShouldBeFalse();
        foreach (FakeSource source in rig.AllSources)
        {
            source.Playing.ShouldBeFalse();
            source.Queue.ShouldBeEmpty();
        }
    }

    [Test]
    public void MissingComponentAndUnreadableFile_AddDiagnosticsOnce()
    {
        SceneNode node = new("Silent");
        CutsceneAsset asset = new() { Duration = T(2) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
        AudioTrack track = new() { Name = "Music", BindingSlot = 0 };
        track.Clips.Add(new AudioTrackClip { Start = T(0), Duration = T(2), SourcePath = "missing.flac" });
        asset.Tracks.Add(track);

        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        using CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
        CutscenePlayer player = new(compiled, table, new CutsceneStateSnapshot(table));
        player.Play();
        player.Tick(0.02);
        player.Tick(0.02);
        player.Diagnostics.Count.ShouldBe(1);
        player.Diagnostics[0].Message.ShouldContain(nameof(AudioSourceComponent));

        node.AddComponent<AudioSourceComponent>();
        player.Tick(0.02);
        player.Tick(0.02);
        player.Diagnostics.Count.ShouldBe(2);
        player.Diagnostics[1].Message.ShouldContain("missing.flac");
        player.Stop();
    }
    /// <summary>Plays in steps of 20 ms. Before each step, the device reports a heard time at an offset from the clock.</summary>
    private static void PlayWithHeardOffset(Rig rig, int steps, double offsetSeconds)
    {
        const long restartFrame = 160;
        rig.Player.Play();
        rig.Player.Tick(0.02);
        for (int i = 0; i < steps; i++)
        {
            double heard = Math.Max(0.02, rig.Player.Position.ToSeconds() + 0.02 + offsetSeconds);
            rig.SetHeardTime(heard, restartFrame);
            rig.Player.Tick(0.02);
        }
    }

    [Test]
    public void Events_FireOnce_WhileTheAudioPullsTheClockBack()
    {
        using Rig rig = MakeRig(ECutsceneEventSeekPolicy.Skip, 0.1, 0.25);
        PlayWithHeardOffset(rig, 20, -0.03);

        rig.Fired.ShouldBe(["0.1", "0.25"]);
        rig.Discontinuities.ShouldBeEmpty();
        rig.Player.Position.ShouldBeGreaterThan(T(0.25));
    }

    [Test]
    public void Events_FireOnce_WhileTheAudioPullsTheClockForward()
    {
        using Rig rig = MakeRig(ECutsceneEventSeekPolicy.Skip, 0.1, 0.25);
        PlayWithHeardOffset(rig, 20, 0.03);

        rig.Fired.ShouldBe(["0.1", "0.25"]);
        rig.Discontinuities.ShouldBeEmpty();
    }

    [Test]
    public void Events_FireOnce_AfterAReverseStepAndForwardPlayback()
    {
        // The high-water mark keeps play dispatch from firing an event again below the highest position that playback reached.
        using Rig rig = MakeRig(ECutsceneEventSeekPolicy.Skip, 0.05);
        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.SetHeardTime(0.06, 160);
        rig.Player.Tick(0.04);
        rig.Fired.ShouldBe(["0.05"]);

        rig.Player.SetRate(-1.0);
        rig.Player.Tick(0.03);
        rig.Player.Position.ShouldBeLessThan(T(0.05));

        rig.Player.SetRate(1.0);
        rig.Player.Tick(0.02);
        rig.Player.Tick(0.02);
        rig.Player.Position.ShouldBeGreaterThan(T(0.05));
        rig.Fired.ShouldBe(["0.05"]);
    }
    [Test]
    public void HardResync_RaisesADiscontinuity_AndSkipsTheCrossedEvents()
    {
        using Rig rig = MakeRig(ECutsceneEventSeekPolicy.Skip, 0.3);
        rig.Player.Play();
        rig.Player.Tick(0.02);

        rig.SetHeardTime(0.5, 160);
        rig.Player.Tick(0.02);

        rig.Player.Position.ShouldBe(T(0.5));
        rig.Discontinuities.Count.ShouldBe(1);
        rig.Discontinuities[0].Intent.ShouldBe(ESeekIntent.Jump);
        rig.Discontinuities[0].To.ShouldBe(T(0.5));
        rig.Fired.ShouldBeEmpty();

        // Later playback does not fire the skipped event.
        rig.SetHeardTime(0.52, 160);
        rig.Player.Tick(0.02);
        rig.Fired.ShouldBeEmpty();
    }

    [Test]
    public void HardResync_WithFireLatest_FiresTheLastCrossedEventOnce()
    {
        using Rig rig = MakeRig(ECutsceneEventSeekPolicy.FireLatest, 0.2, 0.3);
        rig.Player.Play();
        rig.Player.Tick(0.02);

        rig.SetHeardTime(0.5, 160);
        rig.Player.Tick(0.02);
        rig.SetHeardTime(0.52, 160);
        rig.Player.Tick(0.02);

        rig.Fired.ShouldBe(["0.3 seek"]);
        rig.Discontinuities.Count.ShouldBe(1);
    }

    [Test]
    public void HeardFrame_IsNotBelowTheRestartFrame()
    {
        using Rig rig = MakeRig();
        rig.Player.Seek(T(1.0), ESeekIntent.Jump);
        rig.Player.Play();
        rig.Player.Tick(0.0);

        // The device has played nothing of the new queue yet, and 300 ms of output is still ahead of the listener.
        foreach (FakeTransport transport in rig.Transports)
        {
            transport.SetSampleFrame(0);
            transport.LatencyNanoseconds = 300_000_000;
        }
        rig.Player.Tick(0.02);

        rig.Discontinuities.Count.ShouldBe(1);
        rig.Player.Position.ShouldBeGreaterThanOrEqualTo(T(1.0));
        rig.Player.Position.ShouldBeLessThanOrEqualTo(T(1.02));
    }

    [Test]
    public void PauseAndRateChange_ResumeWithoutABackwardDecoderSeek()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.Player.Tick(0.02);

        rig.Player.Pause();
        rig.Player.Tick(0.02);
        rig.Player.Play();
        foreach (FakeTransport transport in rig.Transports)
            transport.ClearLogs();
        rig.Player.Tick(0.02);
        int frame = (int)rig.Player.Position.ToSamples(SampleRate);
        foreach (FakeSource source in rig.AllSources)
            FirstQueuedSample(source).ShouldBe(frame);

        rig.Player.SetRate(2.0);
        rig.Player.Tick(0.02);
        rig.Player.SetRate(1.0);
        foreach (FakeTransport transport in rig.Transports)
            transport.ClearLogs();
        rig.Player.Tick(0.02);
        frame = (int)rig.Player.Position.ToSamples(SampleRate);
        foreach (FakeSource source in rig.AllSources)
            FirstQueuedSample(source).ShouldBe(frame);

        rig.Decoders.Count.ShouldBe(1);
        rig.Decoders[0].BackwardSeeks.ShouldBe(0);
    }

    [Test]
    public void ScrubSeekWhilePlaying_IsSilent()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);

        rig.Player.Seek(T(1.0), ESeekIntent.Scrub);
        rig.Player.Tick(0.0);

        rig.Player.IsAudioMuted.ShouldBeTrue();
        foreach (FakeSource source in rig.AllSources)
        {
            source.Playing.ShouldBeFalse();
            source.Gain.ShouldBe(0.0f);
        }
    }

    [Test]
    public void Release_RestoresThePlayOnQueueFlagOfEachSource()
    {
        using Rig rig = MakeRig();
        AudioSource[] sources = [.. rig.Component.ActiveListeners.Values];
        sources[0].AutoPlayOnQueue = false;
        sources[1].AutoPlayOnQueue = true;

        rig.Player.Play();
        rig.Player.Tick(0.02);
        sources[0].AutoPlayOnQueue.ShouldBeFalse();
        sources[1].AutoPlayOnQueue.ShouldBeFalse();

        rig.Player.Stop();
        sources[0].AutoPlayOnQueue.ShouldBeFalse();
        sources[1].AutoPlayOnQueue.ShouldBeTrue();
    }

    [Test]
    public void SteadyStateRefillTick_DoesNotAllocate()
    {
        using Rig rig = MakeRig();
        foreach (FakeTransport transport in rig.Transports)
            transport.Recording = false;
        rig.Player.Play();
        rig.Player.Tick(0.02);

        // Warm up the buffer pools and the queues with a few refills.
        for (int i = 0; i < 4; i++)
        {
            foreach (FakeTransport transport in rig.Transports)
                transport.ConsumeOneBuffer();
            rig.SetHeardTime(rig.Player.Position.ToSeconds() + 0.02, 160 + i * AudioTrackEvaluator.ChunkFrames);
            rig.Player.Tick(0.02);
        }

        foreach (FakeTransport transport in rig.Transports)
            transport.ConsumeOneBuffer();
        rig.SetHeardTime(rig.Player.Position.ToSeconds() + 0.02, 160 + 4 * AudioTrackEvaluator.ChunkFrames);
        long before = GC.GetAllocatedBytesForCurrentThread();
        rig.Player.Tick(0.02);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
        rig.Discontinuities.ShouldBeEmpty();
        foreach (FakeSource source in rig.AllSources)
            source.Queue.Count.ShouldBe(4);
    }
}