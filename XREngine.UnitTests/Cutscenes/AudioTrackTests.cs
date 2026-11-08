using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Data;
using XREngine.Rendering;
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
        public bool Starved;
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

        /// <summary>The number of calls on a source handle after the source was destroyed. A correct owner makes none.</summary>
        public int UseAfterDestroy { get; private set; }

        // A call on a destroyed source is a defect of the caller. The fake counts it, so a test can assert zero,
        // and answers with a detached state, so the teardown of a failed test can still run.
        private FakeSource Get(AudioSourceHandle source)
        {
            if (Sources.TryGetValue(source.Id, out FakeSource? state))
                return state;
            UseAfterDestroy++;
            return new FakeSource();
        }
        public bool ClockAvailable { get; set; } = true;

        /// <summary>True to keep a source stopped after Play, as a device that stops every source does.</summary>
        public bool IgnorePlay { get; set; }

        /// <summary>True to count each queued buffer as played at once, as a source that drops its buffers does.</summary>
        public bool ProcessEveryQueuedBuffer { get; set; }
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
            FakeSource s = Get(source);
            s.Playing = !IgnorePlay;
            s.Starved = false;
            if (Recording)
                s.Log.Add("play");
        }

        public void Stop(AudioSourceHandle source)
        {
            FakeSource s = Get(source);
            s.Playing = false;
            s.Processed = s.Queue.Count;
            if (Recording)
                s.Log.Add("stop");
        }

        public void Pause(AudioSourceHandle source) => Get(source).Playing = false;

        public void Rewind(AudioSourceHandle source)
        {
            FakeSource s = Get(source);
            s.Playing = false;
            s.Starved = false;
        }

        public void SetSourceBuffer(AudioSourceHandle source, AudioBufferHandle buffer) { }

        public void QueueBuffers(AudioSourceHandle source, ReadOnlySpan<AudioBufferHandle> buffers)
        {
            FakeSource s = Get(source);
            foreach (AudioBufferHandle buffer in buffers)
            {
                s.Queue.Add(buffer.Id);

                // OpenAL Soft counts a buffer that is queued on a source that ran out of data as played at once.
                if (s.Starved || ProcessEveryQueuedBuffer)
                    s.Processed++;
                if (Recording)
                    s.Log.Add($"queue {_firstSample[buffer.Id]}");
            }
        }

        public int UnqueueProcessedBuffers(AudioSourceHandle source, Span<AudioBufferHandle> output)
        {
            FakeSource s = Get(source);
            int count = Math.Min(Math.Min(s.Processed, output.Length), s.Queue.Count);
            for (int i = 0; i < count; i++)
                output[i] = new AudioBufferHandle(s.Queue[i]);
            s.Queue.RemoveRange(0, count);
            s.Processed -= count;
            return count;
        }

        public int GetBuffersProcessed(AudioSourceHandle source) => Get(source).Processed;

        public int GetBuffersQueued(AudioSourceHandle source) => Get(source).Queue.Count;

        public void SetSourcePosition(AudioSourceHandle source, Vector3 position) { }
        public void SetSourceVelocity(AudioSourceHandle source, Vector3 velocity) { }
        public void SetSourceGain(AudioSourceHandle source, float gain) => Get(source).Gain = gain;
        public void SetSourcePitch(AudioSourceHandle source, float pitch) { }
        public void SetSourceLooping(AudioSourceHandle source, bool loop) { }
        public bool IsSourcePlaying(AudioSourceHandle source) => Get(source).Playing;
        public int GetSampleOffset(AudioSourceHandle source) => (int)Get(source).SampleFrame;

        public bool TryGetPlaybackClock(AudioSourceHandle source, out long sampleFrame, out long latencyNanoseconds)
        {
            sampleFrame = 0;
            latencyNanoseconds = 0;
            if (!ClockAvailable)
                return false;
            sampleFrame = Get(source).SampleFrame;
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

        /// <summary>Plays every queued buffer of each source and stops it, as the device does when the queue runs out.</summary>
        public void StarveAll()
        {
            foreach (FakeSource source in Sources.Values)
            {
                source.Playing = false;
                source.Starved = true;
                source.Processed = source.Queue.Count;
            }
        }

        /// <summary>Marks the first queued buffer of each source as played, as the device does.</summary>
        public void ConsumeOneBuffer()
        {
            foreach (FakeSource source in Sources.Values)
                source.Processed = Math.Min(source.Queue.Count, source.Processed + 1);
        }
    }

    /// <summary>
    /// A wall clock that moves only when a fake decoder moves it. A decoder that opens takes <see cref="NewDecoderSeekTime"/> as its seek time.
    /// </summary>
    private sealed class FakeWallClock : TimeProvider
    {
        private long _ticks;

        public TimeSpan NewDecoderSeekTime { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan time)
            => _ticks += time.Ticks;
    }

    /// <summary>
    /// A decoder that wraps another decoder and counts the seeks that go backward from its read position.
    /// Each seek moves the fake wall clock by <see cref="SeekTime"/>, as a slow decoder seek does.
    /// </summary>
    private sealed class CountingDecoder(IStreamingAudioDecoder inner, FakeWallClock wallClock) : IStreamingAudioDecoder
    {
        private long _position;

        public int BackwardSeeks { get; private set; }
        public int Seeks { get; private set; }
        public TimeSpan SeekTime { get; set; }
        public TimeSpan ReadTime { get; set; }
        public int SampleRate => inner.SampleRate;
        public int Channels => inner.Channels;
        public long TotalFrames => inner.TotalFrames;

        public void SeekToFrame(long frame)
        {
            Seeks++;
            if (frame < _position)
                BackwardSeeks++;
            wallClock.Advance(SeekTime);
            inner.SeekToFrame(frame);
            _position = Math.Clamp(frame, 0L, TotalFrames);
        }

        public int Read(Span<float> destination)
        {
            wallClock.Advance(ReadTime);
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
        public AudioSourceComponent? MusicComponent { get; init; }
        public required FakeTransport[] Transports { get; init; }
        public required ListenerContext[] Listeners { get; init; }
        public required CutsceneBindingTable Table { get; init; }
        public required List<CountingDecoder> Decoders { get; init; }
        public required List<string> Fired { get; init; }
        public required List<CutsceneDiscontinuity> Discontinuities { get; init; }
        public required FakeWallClock WallClock { get; init; }

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
            foreach (FakeTransport transport in Transports)
                transport.UseAfterDestroy.ShouldBe(0);
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
        => MakeRig(null, policy, eventTimes);

    private static Rig MakeRig((double Start, double Duration, double Offset)? music, ECutsceneEventSeekPolicy policy, double[] eventTimes)
        => MakeRig(music, musicFirst: false, policy, eventTimes);

    /// <summary>
    /// Makes the rig. A music clip adds a second audio track on its own node, with <see cref="AudioTrack.DrivesClock"/> false.
    /// The evaluator services the tracks in their order. The dialog track comes first unless <paramref name="musicFirst"/> is true.
    /// </summary>
    private static Rig MakeRig((double Start, double Duration, double Offset)? music, bool musicFirst, ECutsceneEventSeekPolicy policy, double[] eventTimes)
        => MakeRig(music, musicFirst, policy, eventTimes, extend: null);

    /// <summary>
    /// Makes the rig. <paramref name="extend"/> can add tracks and bindings to the asset and returns the nodes it binds.
    /// <paramref name="openDecoder"/> replaces the counting WAV decoder of each clip; the rig then records no decoders.
    /// </summary>
    private static Rig MakeRig(
        (double Start, double Duration, double Offset)? music,
        bool musicFirst,
        ECutsceneEventSeekPolicy policy,
        double[] eventTimes,
        Func<CutsceneAsset, SceneNode[]>? extend,
        Func<string, IStreamingAudioDecoder>? openDecoder = null)
    {
        string path = WriteRampWav(SampleRate * 3);
        SceneNode node = new("Speaker");
        AudioSourceComponent component = node.AddComponent<AudioSourceComponent>()!;
        SceneNode musicNode = new("Music");
        AudioSourceComponent? musicComponent = music is null ? null : musicNode.AddComponent<AudioSourceComponent>()!;

        FakeTransport[] transports = [new(), new()];
        ListenerContext[] listeners = [new(transports[0], null), new(transports[1], null)];
        foreach (ListenerContext listener in listeners)
        {
            component.AddSourceToListener(listener);
            musicComponent?.AddSourceToListener(listener);
        }

        CutsceneAsset asset = new() { Duration = T(3) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
        AudioTrack track = new() { Name = "Dialog", BindingSlot = 0 };
        track.Clips.Add(new AudioTrackClip { Start = T(0), Duration = T(3), SourcePath = path });
        asset.Tracks.Add(track);
        if (music is { } m)
        {
            asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = musicNode.ID });
            AudioTrack musicTrack = new() { Name = "Music", BindingSlot = 1, DrivesClock = false };
            musicTrack.Clips.Add(new AudioTrackClip { Start = T(m.Start), Duration = T(m.Duration), ClipOffset = T(m.Offset), SourcePath = path });
            asset.Tracks.Insert(musicFirst ? 0 : asset.Tracks.Count, musicTrack);
        }
        if (eventTimes.Length > 0)
        {
            EventTrack events = new() { Name = "Events", SeekPolicy = policy };
            foreach (double time in eventTimes)
                events.Events.Add(new CutsceneEvent { Time = T(time), Key = "e" });
            asset.Tracks.Add(events);
        }

        SceneNode[] extraNodes = extend?.Invoke(asset) ?? [];

        List<CutsceneCompileDiagnostic> diagnostics = [];
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, diagnostics);
        diagnostics.ShouldBeEmpty();
        CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, new FakeResolver([node, musicNode, .. extraNodes]));
        table.Diagnostics.ShouldBeEmpty();
        List<CountingDecoder> decoders = [];
        FakeWallClock wallClock = new();
        CutscenePlayer player = new(compiled, table, new CutsceneStateSnapshot(table), openDecoder ?? (file =>
        {
            CountingDecoder decoder = new(AudioTrackEvaluator.OpenDefaultDecoder(file), wallClock) { SeekTime = wallClock.NewDecoderSeekTime };
            decoders.Add(decoder);
            return decoder;
        }), wallClock);
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
            MusicComponent = musicComponent,
            Transports = transports,
            Listeners = listeners,
            Table = table,
            Decoders = decoders,
            Fired = fired,
            Discontinuities = discontinuities,
            WallClock = wallClock,
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
    [CancelAfter(60000)]
    public void VorbisIndexFailureInARead_AddsExactlyOneDiagnostic()
    {
        // The two-block Vorbis file is 3 s at 44100 Hz. The decoder builds its index before the clip opens.
        byte[] file = Convert.FromBase64String(VorbisTwoBlockTestData.Base64);
        const int Rate = 44100;
        const long Shift = 17640;
        VorbisStreamingDecoder? vorbis = null;
        using Rig rig = MakeRig(null, musicFirst: false, ECutsceneEventSeekPolicy.Skip, [], extend: null, openDecoder: _ =>
        {
            vorbis = new VorbisStreamingDecoder(new MemoryStream(file, writable: false), leaveOpen: false, () => new MemoryStream(file, writable: false));
            vorbis.WaitForSeekIndex(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            return vorbis;
        });

        rig.Player.Seek(T(2.9), ESeekIntent.Jump);
        rig.Player.Play();
        rig.Player.Tick(0.0);
        vorbis.ShouldNotBeNull();
        vorbis.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Ready);

        // Move the restart frame of the entry for 2.5 s back by 0.4 s, as if the entry were wrong. The backward seek then
        // ends at the right frame by count, but the decode reaches the end of the file 0.4 s early, in a read.
        long target = (long)(2.5 * Rate);
        vorbis.PageIndex!.TryFind(target, out VorbisPageIndexEntry entry, out int index).ShouldBeTrue();
        index.ShouldBeGreaterThan(0);
        vorbis.PageIndex.Entries[index] = entry with { RestartFrame = entry.RestartFrame - Shift };

        int indexedBefore = vorbis.IndexedSeekCount;
        rig.Player.Seek(T(2.5), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        vorbis.IndexedSeekCount.ShouldBe(indexedBefore + 1);
        vorbis.SeekIndexState.ShouldBe(EVorbisSeekIndexState.Failed);

        // More seeks and reads do not add a second diagnostic.
        rig.Player.Seek(T(1.0), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        rig.Player.Seek(T(0.5), ESeekIntent.Jump);
        rig.Player.Tick(0.0);

        int count = 0;
        foreach (CutsceneCompileDiagnostic diagnostic in rig.Player.Diagnostics)
        {
            if (diagnostic.Message.Contains("Ogg page index", StringComparison.Ordinal))
                count++;
        }
        TestContext.Out.WriteLine(vorbis.SeekIndexFailure);
        count.ShouldBe(1);
        vorbis.SeekIndexFailure!.ShouldContain("before the end of the file");
    }

    [Test]
    [CancelAfter(60000)]
    public void VorbisStreamWithNoSecondOpen_AddsExactlyOneDiagnostic()
    {
        // A buffered stream cannot be opened a second time, so the decoder has no page index. The track reports it one time.
        byte[] file = Convert.FromBase64String(VorbisTwoBlockTestData.Base64);
        using Rig rig = MakeRig(null, musicFirst: false, ECutsceneEventSeekPolicy.Skip, [], extend: null,
            openDecoder: _ => new VorbisStreamingDecoder(new BufferedStream(new MemoryStream(file, writable: false)), leaveOpen: false));

        rig.Player.Seek(T(1.0), ESeekIntent.Jump);
        rig.Player.Play();
        rig.Player.Tick(0.0);
        rig.Player.Seek(T(0.5), ESeekIntent.Jump);
        rig.Player.Tick(0.0);

        int count = 0;
        foreach (CutsceneCompileDiagnostic diagnostic in rig.Player.Diagnostics)
        {
            if (diagnostic.Message.Contains("cannot be opened a second time", StringComparison.Ordinal))
                count++;
        }
        count.ShouldBe(1);
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
    public void PauseRateAndScrub_SilenceTheSources_WithoutATick()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);

        // A preview host does not tick a paused player, so the sources must stop in the call itself.
        rig.Player.Pause();
        foreach (FakeSource source in rig.AllSources)
        {
            source.Playing.ShouldBeFalse();
            source.Gain.ShouldBe(0.0f);
        }

        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.AllSources.ShouldAllBe(source => source.Playing);
        rig.Player.SetRate(2.0);
        rig.AllSources.ShouldAllBe(source => !source.Playing && source.Gain == 0.0f);

        rig.Player.SetRate(1.0);
        rig.Player.Tick(0.02);
        rig.AllSources.ShouldAllBe(source => source.Playing);
        rig.Player.BeginScrub();
        rig.AllSources.ShouldAllBe(source => !source.Playing && source.Gain == 0.0f);
    }

    [Test]
    public void StarvedSource_RestartsAtTheCutsceneTime_AndDoesNotDecodeTheRestOfTheFile()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);

        // A long frame lets the queue run out. A buffer queued on such a source is played at once.
        foreach (FakeTransport transport in rig.Transports)
        {
            transport.StarveAll();
            transport.ClearLogs();
        }
        rig.Player.Tick(0.5);

        foreach (FakeSource source in rig.AllSources)
        {
            source.Playing.ShouldBeTrue();
            source.Queue.Count.ShouldBe(4);
            source.Processed.ShouldBe(0);
            FirstQueuedSample(source).ShouldBeInRange((int)(rig.Player.Position.ToSeconds() * SampleRate) - 1, (int)(rig.Player.Position.ToSeconds() * SampleRate) + 1);
        }
        rig.Player.AudioHardResyncCount.ShouldBe(0);
    }

    [Test]
    public void ClockReadings_ReportTheDifferenceTheLatencyAndTheResyncs()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.Player.AudioClockReadCount.ShouldBe(0);

        // The heard frame is 160 + 4000 - 400 = 3760. The clock is at 0.04 s before the correction, so the clock jumps.
        foreach (FakeTransport transport in rig.Transports)
        {
            transport.SetSampleFrame(4000);
            transport.LatencyNanoseconds = 50_000_000;
        }
        rig.Player.Tick(0.02);
        rig.Player.AudioClockDifference.ShouldBe(CutsceneTime.FromSamples(3760, SampleRate) - T(0.04));
        rig.Player.AudioClockReadCount.ShouldBe(1);
        rig.Player.AudioHardResyncCount.ShouldBe(1);
        rig.Player.AudioClockLatencyNanoseconds.ShouldBe(50_000_000);

        // The next step advances the clock to 0.49 s. The heard frame 3960 is 0.495 s, so the difference is 5 ms and the clock does not jump.
        foreach (FakeTransport transport in rig.Transports)
            transport.SetSampleFrame(4200);
        rig.Player.Tick(0.02);
        rig.Player.AudioClockDifference.ShouldBe(CutsceneTime.FromSamples(3960, SampleRate) - CutsceneTime.FromSamples(3760, SampleRate) - T(0.02));
        rig.Player.AudioClockReadCount.ShouldBe(2);
        rig.Player.AudioHardResyncCount.ShouldBe(1);
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
        // Each step advances 20 ms and the correction pulls 1 ms back, so the step ends are 0.02 + 0.019 k.
        // The event is 0.5 ms after the end of step 5, inside the 1 ms band of that correction. A correction applied after
        // the dispatch would start the next step below the event and fire it a second time.
        using Rig rig = MakeRig(ECutsceneEventSeekPolicy.Skip, 0.1155);
        PlayWithHeardOffset(rig, 20, -0.03);

        rig.Fired.Count.ShouldBe(1);
        rig.Discontinuities.ShouldBeEmpty();
        rig.Player.Position.ShouldBeGreaterThan(T(0.25));
    }

    [Test]
    public void Events_FireOnce_WhileTheAudioPullsTheClockForward()
    {
        // The correction pulls 1 ms forward, so the step ends are 0.02 + 0.021 k. The event is 0.5 ms before the end of step 5.
        // A correction applied after the dispatch would start the next step above the event, so the event would never fire.
        using Rig rig = MakeRig(ECutsceneEventSeekPolicy.Skip, 0.1245);
        PlayWithHeardOffset(rig, 20, 0.03);

        rig.Fired.Count.ShouldBe(1);
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

    /// <summary>Counts simulation resets. The fields do not allocate.</summary>
    private sealed class CountingSimulationTarget : ICutsceneSimulationTarget
    {
        public readonly object Identity = new();
        public int Resets;

        public object Source => Identity;
        public string Name => "counting simulation";
        public bool SupportsWarmUp => false;
        public void ResetToCurrentPose(int seed) => Resets++;
        public void SetHeld(bool held) { }
        public void KeepHeld() { }
        public void WarmUp(float seconds, int seed, double budgetMilliseconds) => Resets++;
    }

    private static CameraComponent CreateShotCamera(string name)
    {
        CameraComponent component = new SceneNode(name).AddComponent<CameraComponent>()!;
        XRCamera camera = new(component.Transform, new XRPerspectiveCameraParameters(60f, 1, 0.1f, 100));
        typeof(CameraComponent).GetField("_camera", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(component, new Lazy<XRCamera>(() => camera));
        return component;
    }

    /// <summary>
    /// Adds a simulation track on the speaker node and camera cut shots. Each shot is (start, duration, camera index).
    /// </summary>
    private static Rig MakeResyncRig(CameraComponent[] cameras, params (double Start, double Duration, int Camera)[] shots)
        => MakeRig(null, musicFirst: false, ECutsceneEventSeekPolicy.Skip, [], asset =>
        {
            int firstCameraSlot = asset.Bindings.Count;
            foreach (CameraComponent camera in cameras)
                asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = camera.SceneNode!.ID });

            CameraCutTrack cut = new() { Name = "Cuts", BindingSlot = firstCameraSlot };
            foreach ((double start, double duration, int camera) in shots)
                cut.Clips.Add(new CameraCutClip { Start = T(start), Duration = T(duration), CameraBindingSlot = firstCameraSlot + camera });
            asset.Tracks.Add(cut);
            asset.Tracks.Add(new SimulationControlTrack { Name = "Hair", BindingSlot = 0 });

            SceneNode[] nodes = new SceneNode[cameras.Length];
            for (int i = 0; i < cameras.Length; i++)
                nodes[i] = cameras[i].SceneNode!;
            return nodes;
        });

    [Test]
    public void HardResync_InsideAShot_ResetsOnlyTheTemporalHistory()
    {
        CameraComponent[] cameras = [CreateShotCamera("Shot")];
        using Rig rig = MakeResyncRig(cameras, (0, 3, 0));
        CountingSimulationTarget target = new();
        rig.Player.Simulation.TargetResolver = (_, targets) => targets.Add(target);
        XRViewport viewport = new(null) { AutomaticallyCollectVisible = false, AutomaticallySwapBuffers = false, SetRenderPipelineFromCamera = false };
        CutsceneCameraRouter router = new(rig.Player);
        router.Attach(viewport);
        rig.Player.Play();
        rig.Player.Tick(0.02);

        XRCamera camera = cameras[0].Camera;
        ulong epochBefore = camera.TemporalHistoryEpoch;
        long exposureBefore = camera.AutoExposureResetCount;
        int resetsBefore = target.Resets;
        int discontinuitiesBefore = rig.Discontinuities.Count;

        rig.SetHeardTime(0.5, 160);
        rig.Player.Tick(0.02);

        rig.Player.AudioHardResyncCount.ShouldBe(1);
        rig.Discontinuities.Count.ShouldBe(discontinuitiesBefore + 1);
        rig.Discontinuities[^1].Kind.ShouldBe(ECutsceneDiscontinuityKind.ClockResync);
        camera.TemporalHistoryEpoch.ShouldBe(epochBefore + 1UL);
        camera.AutoExposureResetCount.ShouldBe(exposureBefore);
        target.Resets.ShouldBe(resetsBefore);
        router.Detach();
    }

    [Test]
    public void HardResync_WithACutInTheSameTick_DoesEachResetOnce()
    {
        CameraComponent[] cameras = [CreateShotCamera("Shot A"), CreateShotCamera("Shot B")];
        using Rig rig = MakeResyncRig(cameras, (0, 0.3, 0), (0.3, 2.7, 1));
        CountingSimulationTarget target = new();
        rig.Player.Simulation.TargetResolver = (_, targets) => targets.Add(target);
        XRViewport viewport = new(null) { AutomaticallyCollectVisible = false, AutomaticallySwapBuffers = false, SetRenderPipelineFromCamera = false };
        CutsceneCameraRouter router = new(rig.Player);
        router.Attach(viewport);
        rig.Player.Play();
        rig.Player.Tick(0.02);

        XRCamera next = cameras[1].Camera;
        ulong epochBefore = next.TemporalHistoryEpoch;
        long exposureBefore = next.AutoExposureResetCount;
        int resetsBefore = target.Resets;
        int discontinuitiesBefore = rig.Discontinuities.Count;

        rig.SetHeardTime(0.5, 160);
        rig.Player.Tick(0.02);

        viewport.ActiveCamera.ShouldBeSameAs(next);
        rig.Discontinuities.Count.ShouldBe(discontinuitiesBefore + 1);
        rig.Discontinuities[^1].Kind.ShouldBe(ECutsceneDiscontinuityKind.Full);
        next.TemporalHistoryEpoch.ShouldBe(epochBefore + 1UL);
        next.AutoExposureResetCount.ShouldBe(exposureBefore + 1);
        target.Resets.ShouldBe(resetsBefore + 1);
        router.Detach();
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
    /// <summary>
    /// Plays 30 steps of 20 ms with a 300 ms output latency, as with the NAudio output. For the first 15 steps the device has
    /// mixed nothing of the new queue. After that, the device has mixed the time since the restart plus the latency, so the
    /// heard time is the time since the restart.
    /// </summary>
    private static void PlayThroughWarmUp(Rig rig, double restartSeconds)
    {
        foreach (FakeTransport transport in rig.Transports)
            transport.LatencyNanoseconds = 300_000_000;

        for (int i = 0; i < 30; i++)
        {
            double next = rig.Player.Position.ToSeconds() + 0.02;
            long sampleFrame = i < 15 ? 0 : (long)Math.Round((next - restartSeconds + 0.3) * SampleRate);
            foreach (FakeTransport transport in rig.Transports)
                transport.SetSampleFrame(sampleFrame);
            rig.Player.Tick(0.02);
        }
    }

    [Test]
    public void HighLatencyStart_KeepsWallTimeUntilHeard_WithNoResync()
    {
        using Rig rig = MakeRig(ECutsceneEventSeekPolicy.Skip, 0.15);
        rig.Player.Play();
        rig.Player.Tick(0.0);
        PlayThroughWarmUp(rig, 0.0);

        rig.Discontinuities.ShouldBeEmpty();
        rig.Fired.Count.ShouldBe(1);
        rig.Player.IsAudioClockFallback.ShouldBeFalse();
        rig.Player.Clock.Mode.ShouldBe(ECutsceneClockMode.AudioDisciplined);
        rig.Player.Diagnostics.ShouldBeEmpty();
        Math.Abs(rig.Player.Position.ToSeconds() - 0.6).ShouldBeLessThan(0.002);
    }

    [Test]
    public void HighLatencySeek_KeepsWallTimeUntilHeard_WithNoResync()
    {
        using Rig rig = MakeRig(ECutsceneEventSeekPolicy.Skip, 1.15);
        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.Player.Seek(T(1.0), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        rig.Discontinuities.Count.ShouldBe(1);

        PlayThroughWarmUp(rig, 1.0);

        // Only the seek raised a discontinuity.
        rig.Discontinuities.Count.ShouldBe(1);
        rig.Fired.Count.ShouldBe(1);
        rig.Player.IsAudioClockFallback.ShouldBeFalse();
        rig.Player.Diagnostics.ShouldBeEmpty();
    }

    // The order of the listener table depends on the hash codes, so each listener is removed once. One of the two cases
    // removes the source at the head of the channel list, which the clock reads before the next service.
    [TestCase(0)]
    [TestCase(1)]
    public void ListenerSwapAtTheSameCount_RestartsWithTheNewSource(int removed)
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);

        FakeTransport replacement = new();
        using ListenerContext third = new(replacement, null);
        rig.Component.RemoveSourceFromListener(rig.Listeners[removed]);
        rig.Component.AddSourceToListener(third);
        rig.Component.ActiveListenerCount.ShouldBe(2);
        replacement.ClearLogs();

        rig.Player.Tick(0.02);

        FakeSource source = replacement.Sources.Values.Single();
        source.Playing.ShouldBeTrue();
        source.Queue.Count.ShouldBe(4);
        FirstQueuedSample(source).ShouldBe((int)rig.Player.Position.ToSamples(SampleRate));
        rig.Player.Stop();

        // The removed source went back to its listener pool. The channel wrote nothing to it after the removal.
        replacement.UseAfterDestroy.ShouldBe(0);
        foreach (FakeTransport transport in rig.Transports)
            transport.UseAfterDestroy.ShouldBe(0);
        source.Playing.ShouldBeFalse();
    }

    [Test]
    public void WarmUpThatNeverEnds_FallsBackAfterTheBound()
    {
        // The transport answers, but the device never reaches the queue: frame 0 with a 50 ms latency is always before the restart frame.
        // The bound is max(50 ms + 250 ms, 500 ms) = 500 ms of play steps after the restart.
        using Rig rig = MakeRig();
        foreach (FakeTransport transport in rig.Transports)
        {
            transport.SetSampleFrame(0);
            transport.LatencyNanoseconds = 50_000_000;
        }
        rig.Player.Play();
        rig.Player.Tick(0.02);

        for (int i = 0; i < 24; i++)
            rig.Player.Tick(0.02);
        rig.Player.IsAudioClockFallback.ShouldBeFalse();
        rig.Player.Clock.Mode.ShouldBe(ECutsceneClockMode.AudioDisciplined);
        rig.Player.Diagnostics.ShouldBeEmpty();

        for (int i = 0; i < 3; i++)
            rig.Player.Tick(0.02);
        rig.Player.IsAudioClockFallback.ShouldBeTrue();
        rig.Player.Clock.Mode.ShouldBe(ECutsceneClockMode.Wall);
        rig.Player.Diagnostics.Count.ShouldBe(1);
        rig.Player.Diagnostics[0].Message.ShouldContain("cannot report a playback clock");

        // More steps add no diagnostic. The clock comes back when the device reaches the queue.
        for (int i = 0; i < 10; i++)
            rig.Player.Tick(0.02);
        rig.Player.Diagnostics.Count.ShouldBe(1);
        foreach (FakeTransport transport in rig.Transports)
            transport.SetSampleFrame(4000);
        rig.Player.Tick(0.02);
        rig.Player.IsAudioClockFallback.ShouldBeFalse();
        rig.Player.Clock.Mode.ShouldBe(ECutsceneClockMode.AudioDisciplined);
    }

    [Test]
    public void WarmUpEnd_StepsTheClockToTheHeardPosition_AndFiresTheEventOnce()
    {
        // The clock runs on wall time while the device does not play the new queue yet. With a 50 ms latency it is then
        // 60 ms ahead of the sound, as on a real device.
        using Rig rig = MakeRig(ECutsceneEventSeekPolicy.Skip, 0.05);
        rig.Player.Play();
        rig.Player.Tick(0.0);
        foreach (FakeTransport transport in rig.Transports)
        {
            transport.SetSampleFrame(0);
            transport.LatencyNanoseconds = 50_000_000;
        }
        for (int i = 0; i < 4; i++)
            rig.Player.Tick(0.02);
        rig.Player.AudioClockReadCount.ShouldBe(0);
        rig.Fired.ShouldBe(["0.05"]);

        // The first heard frame is 720 - 400 = 320 (0.04 s). The clock is at 0.10 s, so it steps back 60 ms with no resync.
        foreach (FakeTransport transport in rig.Transports)
            transport.SetSampleFrame(720);
        rig.Player.Tick(0.02);
        rig.Player.Position.ShouldBe(CutsceneTime.FromSamples(320, SampleRate));

        // On the next reading, the clock and the sound agree. The event behind the step does not fire again.
        foreach (FakeTransport transport in rig.Transports)
            transport.SetSampleFrame(880);
        rig.Player.Tick(0.02);
        Math.Abs(rig.Player.AudioClockDifference.ToSeconds()).ShouldBeLessThan(0.001);
        rig.Player.AudioClockReadCount.ShouldBe(2);
        rig.Player.AudioHardResyncCount.ShouldBe(0);
        rig.Discontinuities.ShouldBeEmpty();
        rig.Fired.ShouldBe(["0.05"]);
    }

    [Test]
    public void LongDecoderSeek_IsNotCountedAsClockTime()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.Player.Tick(0.02);

        // The seek to 2 s is outside the ring of decoded frames, so the decoder seeks. The seek takes 300 ms of wall time,
        // and the host gives that time to the next tick.
        rig.Decoders.Single().SeekTime = TimeSpan.FromMilliseconds(300);
        int seeks = rig.Decoders.Single().Seeks;
        rig.Player.Seek(T(2.0), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        rig.Decoders.Single().Seeks.ShouldBe(seeks + 1);

        // The device played 10 ms of the new queue after the seek.
        rig.SetHeardTime(2.01, 2 * SampleRate);
        rig.Player.Tick(0.31);

        rig.Player.AudioHardResyncCount.ShouldBe(0);
        rig.Discontinuities.Count.ShouldBe(1);
        rig.Player.Position.ShouldBe(CutsceneTime.FromSamples(16080, SampleRate));
        Math.Abs(rig.Player.AudioClockDifference.ToSeconds()).ShouldBeLessThan(0.002);
    }

    [Test]
    public void DrainedSourceAtTheClipEnd_IsNotRead()
    {
        // The seek to 2.9 s leaves 800 frames, so one buffer holds the rest of the clip.
        using Rig rig = MakeRig();
        rig.Player.Seek(T(2.9), ESeekIntent.Jump);
        rig.Player.Play();
        rig.Player.Tick(0.0);
        rig.SetHeardTime(2.92, (long)(2.9 * SampleRate));
        rig.Player.Tick(0.02);
        rig.Player.AudioClockReadCount.ShouldBe(1);

        // The device played the queue and stopped. The listener still hears the output latency, so the frame stays behind the clock.
        foreach (FakeTransport transport in rig.Transports)
        {
            transport.LatencyNanoseconds = 50_000_000;
            transport.StarveAll();
        }
        for (int i = 0; i < 3; i++)
            rig.Player.Tick(0.02);

        rig.Player.AudioClockReadCount.ShouldBe(1);
        rig.Player.AudioHardResyncCount.ShouldBe(0);
        rig.Player.IsAudioClockFallback.ShouldBeFalse();
        rig.Player.Diagnostics.ShouldBeEmpty();
        rig.Player.Position.ShouldBe(T(2.98));
    }

    [Test]
    public void NonClockTrackRestart_DoesNotHoldTheClockBack()
    {
        // The music clip starts at 0.5 s with a 1.5 s offset, so its decoder seeks far. The dialog track drives the clock and plays on.
        using Rig rig = MakeRig((0.5, 1.0, 1.5), ECutsceneEventSeekPolicy.Skip, []);
        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.WallClock.NewDecoderSeekTime = TimeSpan.FromMilliseconds(300);

        // The dialog queue starts at frame 160. The device follows the clock in each step.
        while (rig.Player.Position < T(0.5))
        {
            rig.SetHeardTime(rig.Player.Position.ToSeconds() + 0.02, 160);
            rig.Player.Tick(0.02);
        }
        rig.Decoders.Count.ShouldBe(2);
        rig.Decoders[1].Seeks.ShouldBeGreaterThan(0);

        // The host gives the 300 ms of the music seek to the next tick. The dialog played all of that time.
        CutsceneTime before = rig.Player.Position;
        rig.SetHeardTime(before.ToSeconds() + 0.32, 160);
        rig.Player.Tick(0.32);

        rig.Player.AudioHardResyncCount.ShouldBe(0);
        rig.Discontinuities.ShouldBeEmpty();
        Math.Abs((rig.Player.Position - before).ToSeconds() - 0.32).ShouldBeLessThan(0.001);
    }

    [Test]
    public void JumpThatSeeksBothTracks_RemovesOnlyTheTimeBeforeTheClockTrackStarts()
    {
        using Rig rig = MakeRig((0.0, 3.0, 0.0), ECutsceneEventSeekPolicy.Skip, []);
        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.Player.Tick(0.02);
        foreach (CountingDecoder decoder in rig.Decoders)
            decoder.SeekTime = TimeSpan.FromMilliseconds(300);

        // The dialog track restarts first, so its sources play during the 300 ms music seek after it.
        rig.Player.Seek(T(2.0), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        foreach (CountingDecoder decoder in rig.Decoders)
            decoder.Seeks.ShouldBeGreaterThan(1);

        // The host gives both seeks (600 ms) and 20 ms of play to the next tick. The dialog sound started 320 ms ago.
        rig.SetHeardTime(2.32, 2 * SampleRate);
        rig.Player.Tick(0.62);

        rig.Player.AudioHardResyncCount.ShouldBe(0);
        rig.Discontinuities.Count.ShouldBe(1);
        rig.Player.Position.ShouldBe(CutsceneTime.FromSamples(2 * SampleRate + 2560, SampleRate));
    }

    [Test]
    public void SourceThatNeverPlays_FallsBackAfterTheWarmUpBound()
    {
        // Every source stays stopped after Play, so each service restarts a starved source.
        using Rig rig = MakeRig();
        foreach (FakeTransport transport in rig.Transports)
        {
            transport.IgnorePlay = true;
            transport.Recording = false;
        }
        rig.Player.Play();
        rig.Player.Tick(0.02);

        // The bound is 500 ms of play steps with no latency.
        for (int i = 0; i < 24; i++)
            rig.Player.Tick(0.02);
        rig.Player.IsAudioClockFallback.ShouldBeFalse();

        for (int i = 0; i < 4; i++)
            rig.Player.Tick(0.02);
        rig.Player.IsAudioClockFallback.ShouldBeTrue();
        rig.Player.Clock.Mode.ShouldBe(ECutsceneClockMode.Wall);
        rig.Player.Diagnostics.Count.ShouldBe(1);
        rig.Player.Diagnostics[0].Message.ShouldContain("cannot report a playback clock");

        for (int i = 0; i < 5; i++)
            rig.Player.Tick(0.02);
        rig.Player.Diagnostics.Count.ShouldBe(1);
    }

    // The channel lists the sources in the order of the listener table. The first source drives the clock, so a stop of it
    // restarts every source. A stop of another source restarts that source alone.
    [TestCase(true)]
    [TestCase(false)]
    public void SourceThatStops_RestartsAllSourcesOnlyIfItIsTheFirst(bool stopFirst)
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);

        ListenerContext first = rig.Component.ActiveListeners.First().Key;
        int firstIndex = ReferenceEquals(first, rig.Listeners[0]) ? 0 : 1;
        FakeSource stopped = rig.Transports[stopFirst ? firstIndex : 1 - firstIndex].Sources.Values.Single();
        FakeSource other = rig.Transports[stopFirst ? 1 - firstIndex : firstIndex].Sources.Values.Single();
        stopped.Playing = false;
        foreach (FakeTransport transport in rig.Transports)
            transport.ClearLogs();
        rig.Player.Tick(0.02);

        stopped.Playing.ShouldBeTrue();
        stopped.Log[0].ShouldBe("stop");
        stopped.Log[^1].ShouldBe("play");
        FirstQueuedSample(stopped).ShouldBe(stopFirst ? (int)rig.Player.Position.ToSamples(SampleRate) : 160);
        other.Playing.ShouldBeTrue();
        if (stopFirst)
            other.Log[0].ShouldBe("stop");
        else
            other.Log.ShouldNotContain("stop");
        rig.Player.Diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void SourceThatDropsItsBuffers_DecodesAtMostTheQueueTargetInOneTick()
    {
        // Each queued buffer counts as played at once, so the queue never reaches the target. One fill must still stop at four buffers.
        using Rig rig = MakeRig();
        foreach (FakeTransport transport in rig.Transports)
            transport.ProcessEveryQueuedBuffer = true;
        rig.Player.Play();
        rig.Player.Tick(0.02);
        foreach (FakeSource source in rig.AllSources)
            source.Log.Count(entry => entry.StartsWith("queue ", StringComparison.Ordinal)).ShouldBe(4);

        foreach (FakeTransport transport in rig.Transports)
            transport.ClearLogs();
        rig.Player.Tick(0.02);
        foreach (FakeSource source in rig.AllSources)
            source.Log.Count(entry => entry.StartsWith("queue ", StringComparison.Ordinal)).ShouldBeLessThanOrEqualTo(4);
    }

    [Test]
    public void SecondSourceThatNeverPlays_IsRestartedOnce_AndTheFirstSourceDrivesTheClock()
    {
        using Rig rig = MakeRig();

        // The channel lists the sources in the order of the listener table. The source of the other listener never plays.
        ListenerContext first = rig.Component.ActiveListeners.First().Key;
        FakeTransport dead = ReferenceEquals(first, rig.Listeners[0]) ? rig.Transports[1] : rig.Transports[0];
        FakeTransport live = ReferenceEquals(dead, rig.Transports[0]) ? rig.Transports[1] : rig.Transports[0];
        dead.IgnorePlay = true;
        rig.Player.Play();
        rig.Player.Tick(0.02);
        foreach (FakeTransport transport in rig.Transports)
            transport.ClearLogs();

        for (int i = 0; i < 10; i++)
        {
            rig.SetHeardTime(rig.Player.Position.ToSeconds() + 0.02, 160);
            rig.Player.Tick(0.02);
        }

        FakeSource liveSource = live.Sources.Values.Single();
        liveSource.Playing.ShouldBeTrue();
        liveSource.Log.ShouldNotContain("stop");
        dead.Sources.Values.Single().Log.Count(entry => entry == "play").ShouldBe(1);
        rig.Player.Clock.Mode.ShouldBe(ECutsceneClockMode.AudioDisciplined);
        rig.Player.IsAudioClockFallback.ShouldBeFalse();
        rig.Player.AudioClockReadCount.ShouldBe(10);
        rig.Player.Diagnostics.Count.ShouldBe(1);
        rig.Player.Diagnostics[0].Message.ShouldContain("stopped again after a restart");

        // A full restart, here a seek, gives the dropped source the stream again.
        dead.IgnorePlay = false;
        dead.ClearLogs();
        rig.Player.Seek(T(1.0), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        FakeSource deadSource = dead.Sources.Values.Single();
        deadSource.Playing.ShouldBeTrue();
        deadSource.Queue.Count.ShouldBe(4);
        FirstQueuedSample(deadSource).ShouldBe(SampleRate);
        rig.Player.Diagnostics.Count.ShouldBe(1);
    }

    [Test]
    public void OneLongFrameThatStarvesTheSource_DoesNotFallBack()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.SetHeardTime(0.04, 160);
        rig.Player.Tick(0.02);
        rig.Player.AudioClockReadCount.ShouldBe(1);

        // One 600 ms frame is longer than the 500 ms warm-up bound, and the queue runs out during it.
        foreach (FakeTransport transport in rig.Transports)
            transport.StarveAll();
        rig.Player.Tick(0.6);

        rig.Player.IsAudioClockFallback.ShouldBeFalse();
        rig.Player.Clock.Mode.ShouldBe(ECutsceneClockMode.AudioDisciplined);
        rig.Player.Diagnostics.ShouldBeEmpty();
        rig.AllSources.ShouldAllBe(source => source.Playing);
    }

    [Test]
    public void RepeatedStarvedRestarts_DoNotSlowTheFallbackClock()
    {
        // Each restart reads from the decoder, and each read takes 5 ms of wall time.
        using Rig rig = MakeRig();
        foreach (FakeTransport transport in rig.Transports)
        {
            transport.IgnorePlay = true;
            transport.Recording = false;
        }
        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.Decoders.Single().ReadTime = TimeSpan.FromMilliseconds(5);
        for (int i = 0; i < 30; i++)
            rig.Player.Tick(0.02);
        rig.Player.IsAudioClockFallback.ShouldBeTrue();

        CutsceneTime before = rig.Player.Position;
        rig.Player.Tick(0.02);
        rig.Player.Tick(0.02);
        Math.Abs((rig.Player.Position - before).ToSeconds() - 0.04).ShouldBeLessThan(1e-6);
    }

    [Test]
    public void JumpThatSeeksAnEarlierTrackFirst_RemovesBothRestartTimes()
    {
        // The music track is first in the asset, so the evaluator restarts it before the dialog track that drives the clock.
        using Rig rig = MakeRig((0.0, 3.0, 0.0), musicFirst: true, ECutsceneEventSeekPolicy.Skip, []);
        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.Player.Tick(0.02);
        foreach (CountingDecoder decoder in rig.Decoders)
            decoder.SeekTime = TimeSpan.FromMilliseconds(300);

        rig.Player.Seek(T(2.0), ESeekIntent.Jump);
        rig.Player.Tick(0.0);

        // Both seeks (600 ms) delayed the dialog sound, which started 20 ms before the next tick.
        rig.SetHeardTime(2.02, 2 * SampleRate);
        rig.Player.Tick(0.62);

        rig.Player.AudioHardResyncCount.ShouldBe(0);
        rig.Discontinuities.Count.ShouldBe(1);
        rig.Player.Position.ShouldBe(CutsceneTime.FromSamples(2 * SampleRate + 160, SampleRate));
    }

    [Test]
    public void SoloRestartOnATrackThatDoesNotDriveTheClock_IsAvailableAgainAfterTheSourcePlays()
    {
        // The music track does not drive the clock, and the clock is in Wall mode, so no clock reading happens.
        using Rig rig = MakeRig((0.0, 3.0, 0.0), ECutsceneEventSeekPolicy.Skip, []);
        rig.Player.Clock.Mode = ECutsceneClockMode.Wall;
        rig.Player.Play();
        rig.Player.Tick(0.02);

        // The music source of the listener that is not first in the listener table of the music component.
        AudioSourceComponent music = rig.MusicComponent!;
        ListenerContext first = music.ActiveListeners.First().Key;
        int other = ReferenceEquals(first, rig.Listeners[0]) ? 1 : 0;
        FakeTransport transport = rig.Transports[other];
        FakeSource source = transport.Sources[music.ActiveListeners[rig.Listeners[other]].Handle];

        // The first stop gives a restart of the source alone.
        source.Playing = false;
        transport.ClearLogs();
        rig.Player.Tick(0.02);
        source.Playing.ShouldBeTrue();
        source.Log.Count(entry => entry == "play").ShouldBe(1);

        // The source plays a buffer, and much later it stops again. It gets a new restart and is not dropped.
        transport.ConsumeOneBuffer();
        for (int i = 0; i < 20; i++)
            rig.Player.Tick(0.02);
        source.Playing = false;
        rig.Player.Tick(0.02);

        source.Playing.ShouldBeTrue();
        source.Log.Count(entry => entry == "play").ShouldBe(2);
        rig.Player.Diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void SeekAfterAStarve_ThenALongFrame_DoesNotFallBack()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.02);
        rig.SetHeardTime(0.04, 160);
        rig.Player.Tick(0.02);
        rig.Player.AudioClockReadCount.ShouldBe(1);

        // A long frame starves the source. A seek restarts the sources before any valid reading.
        foreach (FakeTransport transport in rig.Transports)
            transport.StarveAll();
        rig.Player.Tick(0.6);
        rig.Player.Seek(T(1.0), ESeekIntent.Jump);
        rig.Player.Tick(0.0);

        // A second long frame after the seek is a first starve again, so it does not count toward the bound.
        foreach (FakeTransport transport in rig.Transports)
            transport.StarveAll();
        rig.Player.Tick(0.6);

        rig.Player.IsAudioClockFallback.ShouldBeFalse();
        rig.Player.Diagnostics.ShouldBeEmpty();
    }
}
