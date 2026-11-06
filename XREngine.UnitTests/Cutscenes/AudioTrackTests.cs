using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Cutscenes;
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
            s.Log.Add("play");
        }

        public void Stop(AudioSourceHandle source)
        {
            FakeSource s = Sources[source.Id];
            s.Playing = false;
            s.Processed = s.Queue.Count;
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

    private static Rig MakeRig()
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

        List<CutsceneCompileDiagnostic> diagnostics = [];
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, diagnostics);
        diagnostics.ShouldBeEmpty();
        CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
        table.Diagnostics.ShouldBeEmpty();
        CutscenePlayer player = new(compiled, table, new CutsceneStateSnapshot(table));

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
}
