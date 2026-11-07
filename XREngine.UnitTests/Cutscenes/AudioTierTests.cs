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

/// <summary>
/// Tests the audio tier change (proxy at once after a jump, then a crossfade to the full decoder), the scrub grains,
/// the stale proxy rule, and <see cref="AudioResidencyBackend"/>.
/// </summary>
[TestFixture, NonParallelizable]
public sealed class AudioTierTests
{
    private const int Rate = 48000;
    private const int Chunk = AudioTrackEvaluator.ChunkFrames;
    private const double ToneHz = 440.0;
    private const double ToneAmplitude = 0.5;

    /// <summary>A scheduler that queues each task until the test runs it, so a background seek runs at a known time.</summary>
    private sealed class ManualScheduler : TaskScheduler
    {
        private readonly List<Task> _tasks = [];

        public int Pending => _tasks.Count;

        public int RunAll()
        {
            int count = 0;
            while (_tasks.Count > 0)
            {
                Task task = _tasks[0];
                _tasks.RemoveAt(0);
                TryExecuteTask(task);
                count++;
            }
            return count;
        }

        protected override IEnumerable<Task> GetScheduledTasks() => _tasks;

        protected override void QueueTask(Task task) => _tasks.Add(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    /// <summary>A decoder that records its seeks, its reads, and its disposal.</summary>
    private sealed class RecordingDecoder(IStreamingAudioDecoder inner) : IStreamingAudioDecoder
    {
        private volatile bool _disposed;

        public List<long> Seeks { get; } = [];
        public int Reads { get; private set; }
        public bool Disposed => _disposed;

        /// <summary>Runs at the start of each seek, on the thread of the seek. It can throw, block, or call the owner.</summary>
        public Action<long>? BeforeSeek { get; set; }
        public int SampleRate => inner.SampleRate;
        public int Channels => inner.Channels;
        public long TotalFrames => inner.TotalFrames;

        public void SeekToFrame(long frame)
        {
            BeforeSeek?.Invoke(frame);
            Seeks.Add(frame);
            inner.SeekToFrame(frame);
        }

        public int Read(Span<float> destination)
        {
            Reads++;
            return inner.Read(destination);
        }

        public void Dispose()
        {
            _disposed = true;
            inner.Dispose();
        }
    }

    private sealed class FakeResolver(SceneNode node) : ICutsceneBindingResolver
    {
        public SceneNode? FindNodeById(Guid id) => id == node.ID ? node : null;

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    private sealed class NoAssets : ICutsceneResidencyAssetCollector
    {
        public void Collect(SceneNode node, List<CutsceneResidencyAsset> destination)
        {
        }
    }

    private sealed class FakeSource
    {
        public readonly List<uint> Queue = [];
        public int Processed;
        public bool Playing;
    }

    /// <summary>A transport with one state for each source. Stop marks every queued buffer as processed, as OpenAL does.</summary>
    private sealed class FakeTransport : IAudioTransport
    {
        private uint _nextSource = 1;
        private uint _nextBuffer = 1;

        public Dictionary<uint, FakeSource> Sources { get; } = [];

        public string? DeviceName => null;
        public int SampleRate => Rate;
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
        public void DestroyBuffer(AudioBufferHandle buffer) { }
        public void UploadBufferData(AudioBufferHandle buffer, ReadOnlySpan<byte> pcm, int frequency, int channels, SampleFormat format) { }
        public void Play(AudioSourceHandle source) => Sources[source.Id].Playing = true;

        /// <summary>Runs at the start of each stop of a source.</summary>
        public Action? BeforeStop { get; set; }

        public void Stop(AudioSourceHandle source)
        {
            BeforeStop?.Invoke();
            FakeSource s = Sources[source.Id];
            s.Playing = false;
            s.Processed = s.Queue.Count;
        }

        public void Pause(AudioSourceHandle source) => Sources[source.Id].Playing = false;
        public void Rewind(AudioSourceHandle source) => Sources[source.Id].Playing = false;
        public void SetSourceBuffer(AudioSourceHandle source, AudioBufferHandle buffer) { }
        public void QueueBuffers(AudioSourceHandle source, ReadOnlySpan<AudioBufferHandle> buffers)
        {
            FakeSource s = Sources[source.Id];
            foreach (AudioBufferHandle buffer in buffers)
                s.Queue.Add(buffer.Id);
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
        public void SetSourceGain(AudioSourceHandle source, float gain) { }
        public void SetSourcePitch(AudioSourceHandle source, float pitch) { }
        public void SetSourceLooping(AudioSourceHandle source, bool loop) { }
        public bool IsSourcePlaying(AudioSourceHandle source) => Sources[source.Id].Playing;
        public int GetSampleOffset(AudioSourceHandle source) => 0;

        public bool TryGetPlaybackClock(AudioSourceHandle source, out long sampleFrame, out long latencyNanoseconds)
        {
            sampleFrame = 0;
            latencyNanoseconds = 0;
            return true;
        }

        public IAudioCaptureDevice? OpenCaptureDevice(string? device, int sampleRate, SampleFormat format, int bufferSize) => null;

        /// <summary>Marks the first queued buffer of each source as played, as the device does.</summary>
        public void ConsumeOneBuffer()
        {
            foreach (FakeSource source in Sources.Values)
                source.Processed = Math.Min(source.Queue.Count, source.Processed + 1);
        }

        /// <summary>Ends the playing grain of each source: the source played its one buffer and stopped.</summary>
        public void FinishAll()
        {
            foreach (FakeSource source in Sources.Values)
            {
                source.Playing = false;
                source.Processed = source.Queue.Count;
            }
        }
    }

    /// <summary>A cutscene with one audio clip of a 440 Hz tone file at 48 kHz, on a node with one listener.</summary>
    private sealed class Rig : IDisposable
    {
        public required string Directory { get; init; }
        public required string WavPath { get; init; }
        public required CutscenePlayer Player { get; init; }
        public required AudioSourceComponent Component { get; init; }
        public required FakeTransport Transport { get; init; }
        public required ListenerContext Listener { get; init; }
        public required CutsceneBindingTable Table { get; init; }
        public required ManualScheduler Scheduler { get; init; }
        public required List<RecordingDecoder> Decoders { get; init; }
        public required List<(short[] Samples, int Frequency, bool Stereo)> Buffers { get; init; }
        public required Action<(int frequency, bool stereo, short[] buffer)> Capture { get; init; }

        public RecordingDecoder Full => Decoders[^1];

        /// <summary>Plays one buffer on the device and ticks for its time, so the clock follows the heard position.</summary>
        public void PlayOneBuffer()
        {
            Transport.ConsumeOneBuffer();
            Player.Tick(Chunk / (double)Rate);
        }

        public void Dispose()
        {
            Player.Stop();
            Scheduler.RunAll();
            Table.Dispose();
            Listener.Dispose();
            foreach (RecordingDecoder decoder in Decoders)
                decoder.Disposed.ShouldBeTrue();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    private static short ToneSample(long frame)
        => (short)Math.Round(ToneAmplitude * Math.Sin(2.0 * Math.PI * ToneHz * frame / Rate) * 32767.0);

    private static void WriteToneWav(string path, int frames)
    {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream, Encoding.ASCII);
        int dataLength = frames * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        for (int f = 0; f < frames; f++)
            writer.Write(ToneSample(f));
    }

    private static (string Directory, CutsceneAsset Asset) MakeAsset(bool proxy, bool staleProxy, double clipStart = 0.0, double clipOffset = 0.0, bool invalidProxy = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"xre-audio-tier-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        string wav = Path.Combine(directory, "tone.wav");
        WriteToneWav(wav, Rate * 6);

        CutsceneAsset asset = new() { Duration = T(6), FilePath = Path.Combine(directory, "Intro.asset") };
        AudioTrack track = new() { Name = "Dialog", BindingSlot = 0 };
        track.Clips.Add(new AudioTrackClip { Start = T(clipStart), Duration = T(6 - clipStart - clipOffset), ClipOffset = T(clipOffset), SourcePath = "tone.wav" });
        asset.Tracks.Add(track);
        if (proxy)
        {
            using WavStreamingDecoder decoder = new(wav);
            ulong hash = staleProxy ? 12345UL : CutsceneAudioProxySources.HashSourceFile(wav);
            AudioProxyAsset built = AudioProxyBuilder.Build(decoder, "tone.wav", hash);

            // A step index above 88 in the first block header makes the proxy data invalid.
            if (invalidProxy)
                built.EncodedData[2] = 200;
            asset.AudioProxies.Add(built);
        }
        return (directory, asset);
    }

    private static Rig MakeRig(bool proxy = true, bool staleProxy = false, bool invalidProxy = false)
    {
        (string directory, CutsceneAsset asset) = MakeAsset(proxy, staleProxy, invalidProxy: invalidProxy);
        SceneNode node = new("Speaker");
        AudioSourceComponent component = node.AddComponent<AudioSourceComponent>()!;
        FakeTransport transport = new();
        ListenerContext listener = new(transport, null);
        component.AddSourceToListener(listener);
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });

        List<CutsceneCompileDiagnostic> diagnostics = [];
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, diagnostics);
        diagnostics.ShouldBeEmpty();
        CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
        ManualScheduler scheduler = new();
        List<RecordingDecoder> decoders = [];
        CutscenePlayer player = new(compiled, table, new CutsceneStateSnapshot(table), file =>
        {
            RecordingDecoder decoder = new(AudioTrackEvaluator.OpenDefaultDecoder(file));
            decoders.Add(decoder);
            return decoder;
        }, TimeProvider.System, scheduler);

        List<(short[] Samples, int Frequency, bool Stereo)> buffers = [];
        Action<(int frequency, bool stereo, short[] buffer)> capture = e => buffers.Add(((short[])e.buffer.Clone(), e.frequency, e.stereo));
        component.StreamingBufferEnqueuedShort += capture;
        return new Rig
        {
            Directory = directory,
            WavPath = Path.Combine(directory, "tone.wav"),
            Player = player,
            Component = component,
            Transport = transport,
            Listener = listener,
            Table = table,
            Scheduler = scheduler,
            Decoders = decoders,
            Buffers = buffers,
            Capture = capture,
        };
    }

    private static short[] Concatenate(List<(short[] Samples, int Frequency, bool Stereo)> buffers)
    {
        short[] result = new short[buffers.Count * Chunk];
        for (int i = 0; i < buffers.Count; i++)
        {
            buffers[i].Samples.Length.ShouldBe(Chunk);
            buffers[i].Frequency.ShouldBe(Rate);
            buffers[i].Stereo.ShouldBeFalse();
            buffers[i].Samples.CopyTo(result, i * Chunk);
        }
        return result;
    }

    // Starts playback at zero, where the new full decoder needs no seek, and then jumps.
    private static void PlayAndJump(Rig rig, long jumpFrame)
    {
        rig.Player.Play();
        rig.Player.Tick(0.0);
        rig.Full.Seeks.ShouldBeEmpty();
        rig.Buffers.Clear();
        rig.Player.Seek(CutsceneTime.FromSamples(jumpFrame, Rate), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
    }

    [Test]
    public void Jump_PlaysProxySamplesInTheSameFrame_AndSeeksTheFullDecoderInTheBackground()
    {
        using Rig rig = MakeRig();
        const long jump = Rate * 3 + 123;
        rig.Player.Play();
        rig.Player.Tick(0.0);
        int readsBefore = rig.Full.Reads;
        rig.Buffers.Clear();

        rig.Player.Seek(CutsceneTime.FromSamples(jump, Rate), ESeekIntent.Jump);
        rig.Player.Tick(0.0);

        // The update thread did not touch the full decoder. Its seek waits on the background scheduler.
        rig.Full.Seeks.ShouldBeEmpty();
        rig.Full.Reads.ShouldBe(readsBefore);
        rig.Scheduler.Pending.ShouldBe(1);
        rig.Player.Diagnostics.ShouldBeEmpty();

        // The queue holds 4 buffers of the proxy at the full rate, from the exact jump frame.
        short[] samples = Concatenate(rig.Buffers);
        rig.Buffers.Count.ShouldBe(4);
        double maxError = 0.0;
        for (int i = 0; i < samples.Length; i++)
            maxError = Math.Max(maxError, Math.Abs(samples[i] - ToneSample(jump + i)) / 32768.0);
        maxError.ShouldBeLessThan(0.02);
        foreach (FakeSource source in rig.Transport.Sources.Values)
            source.Playing.ShouldBeTrue();

        rig.Scheduler.RunAll().ShouldBe(1);
        rig.Full.Seeks.ShouldBe([jump + 4L * Chunk]);
    }

    [Test]
    public void CrossfadeGains_AreEqualGain()
    {
        int length = (int)Math.Round(AudioTierCrossfader.CrossfadeSeconds * Rate);
        length.ShouldBe(960);
        for (int i = 0; i < length; i++)
        {
            AudioTierCrossfader.GetGains(i, length, out float fadeOut, out float fadeIn);
            Math.Abs(fadeOut + fadeIn - 1.0f).ShouldBeLessThan(1e-3f);
            if (i > 0)
            {
                AudioTierCrossfader.GetGains(i - 1, length, out float previousOut, out float previousIn);
                fadeIn.ShouldBeGreaterThan(previousIn);
                fadeOut.ShouldBeLessThan(previousOut);
            }
        }

        AudioTierCrossfader.GetGains(0, length, out float firstOut, out float firstIn);
        firstIn.ShouldBeLessThan(0.01f);
        firstOut.ShouldBeGreaterThan(0.99f);
        AudioTierCrossfader.GetGains(length - 1, length, out float lastOut, out float lastIn);
        lastOut.ShouldBeLessThan(0.01f);
        lastIn.ShouldBeGreaterThan(0.99f);
    }

    [Test]
    public void TierChange_IsSampleAligned_AtABufferBoundary_WithNoSpike()
    {
        using Rig rig = MakeRig();
        const long jump = Rate * 3 + 123;
        PlayAndJump(rig, jump);
        rig.Scheduler.RunAll();

        // Each played buffer adds one buffer. The fifth buffer starts where the full decoder waits.
        for (int i = 0; i < 4; i++)
            rig.PlayOneBuffer();

        rig.Player.AudioHardResyncCount.ShouldBe(0);
        rig.Player.Diagnostics.ShouldBeEmpty();
        short[] samples = Concatenate(rig.Buffers);
        samples.Length.ShouldBe(8 * Chunk);
        int change = 4 * Chunk;
        int fade = (int)Math.Round(AudioTierCrossfader.CrossfadeSeconds * Rate);

        // Before the change and in the crossfade, the output follows the tone: the two signals are aligned and the gains
        // sum to one, so the level does not change. After the crossfade, the output is the full decoder, sample for sample.
        for (int n = 0; n < samples.Length; n++)
        {
            if (n >= change + fade)
            {
                samples[n].ShouldBe(ToneSample(jump + n), $"frame {n}");
                continue;
            }
            ((double)Math.Abs(samples[n] - ToneSample(jump + n))).ShouldBeLessThan(0.02 * 32768.0, $"frame {n}");
        }

        // No step at the change: the largest difference near the change is not above the largest difference of the tone.
        double reference = 0.0;
        for (int n = change - 1500; n < change - 100; n++)
            reference = Math.Max(reference, Math.Abs(samples[n] - samples[n - 1]));
        double nearChange = 0.0;
        for (int n = change - 32; n <= change + 32; n++)
            nearChange = Math.Max(nearChange, Math.Abs(samples[n] - samples[n - 1]));
        nearChange.ShouldBeLessThan(reference * 1.15);
    }

    [Test]
    public void NewerJump_CancelsThePendingFullDecoderSeek()
    {
        using Rig rig = MakeRig();
        const long first = Rate * 3;
        const long second = Rate * 1 + 77;
        PlayAndJump(rig, first);
        rig.Scheduler.Pending.ShouldBe(1);

        rig.Buffers.Clear();
        rig.Player.Seek(CutsceneTime.FromSamples(second, Rate), ESeekIntent.Jump);
        rig.Player.Tick(0.0);

        // The waiting seek of the first jump never runs: the task seeks only to the frame of the newer jump.
        rig.Scheduler.RunAll();
        rig.Full.Seeks.ShouldBe([second + 4L * Chunk]);

        for (int i = 0; i < 4; i++)
            rig.PlayOneBuffer();
        short[] samples = Concatenate(rig.Buffers);
        int afterFade = 4 * Chunk + 960;
        for (int n = afterFade; n < samples.Length; n++)
            samples[n].ShouldBe(ToneSample(second + n), $"frame {n}");
    }

    [Test]
    public void NewerJump_DiscardsTheResultOfACompleteSeekThatWasNotUsed()
    {
        using Rig rig = MakeRig();
        const long first = Rate * 3;
        const long second = Rate * 4 + 5;
        PlayAndJump(rig, first);
        rig.Scheduler.RunAll();

        rig.Buffers.Clear();
        rig.Player.Seek(CutsceneTime.FromSamples(second, Rate), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        rig.Scheduler.RunAll();
        rig.Full.Seeks.ShouldBe([first + 4L * Chunk, second + 4L * Chunk]);

        for (int i = 0; i < 4; i++)
            rig.PlayOneBuffer();
        short[] samples = Concatenate(rig.Buffers);
        for (int n = 4 * Chunk + 960; n < samples.Length; n++)
            samples[n].ShouldBe(ToneSample(second + n), $"frame {n}");
    }

    [Test]
    public void ProxyPlaybackAndTheTierChange_DoNotAllocate()
    {
        // A proxy refill: the background seek has not run, so each new buffer comes from the proxy.
        using (Rig rig = MakeRig())
        {
            PlayAndJump(rig, Rate * 3);
            rig.Component.StreamingBufferEnqueuedShort -= rig.Capture;
            rig.PlayOneBuffer();
            long before = GC.GetAllocatedBytesForCurrentThread();
            rig.PlayOneBuffer();
            (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0L);
        }

        // A seek that ends after its buffer passed asks again with twice the lead. The first refill after a restart runs
        // before the measure. Then the refill with the crossfade, and a full refill after it, do not allocate.
        using (Rig rig = MakeRig())
        {
            const long jump = Rate * 3;
            PlayAndJump(rig, jump);
            int readsAtJump = rig.Full.Reads;
            rig.Component.StreamingBufferEnqueuedShort -= rig.Capture;
            rig.PlayOneBuffer();
            rig.Scheduler.RunAll();
            rig.PlayOneBuffer();
            rig.Scheduler.RunAll();
            rig.Full.Seeks.ShouldBe([jump + 4L * Chunk, jump + 13L * Chunk]);
            for (int i = 0; i < 7; i++)
                rig.PlayOneBuffer();
            rig.Full.Reads.ShouldBe(readsAtJump);

            long before = GC.GetAllocatedBytesForCurrentThread();
            rig.PlayOneBuffer();
            rig.PlayOneBuffer();
            (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0L);
            rig.Full.Reads.ShouldBeGreaterThan(readsAtJump);
        }
    }

    [Test]
    public void Grain_HasZeroAmplitudeAtBothEnds()
    {
        int frames = (int)Math.Round(AudioScrubGrainPlayer.GrainSeconds * Rate);
        int fade = (int)Math.Round(AudioScrubGrainPlayer.FadeSeconds * Rate);
        AudioScrubGrainPlayer.GetFadeGain(0, frames, fade).ShouldBe(0.0f);
        AudioScrubGrainPlayer.GetFadeGain(frames - 1, frames, fade).ShouldBe(0.0f);
        AudioScrubGrainPlayer.GetFadeGain(frames / 2, frames, fade).ShouldBe(1.0f);

        using Rig rig = MakeRig();
        rig.Player.Seek(T(2.0), ESeekIntent.Scrub);
        rig.Player.Tick(0.0);

        rig.Buffers.Count.ShouldBe(1);
        short[] grain = rig.Buffers[0].Samples;
        grain.Length.ShouldBe(frames);
        grain[0].ShouldBe((short)0);
        grain[^1].ShouldBe((short)0);
        short peak = 0;
        for (int i = fade; i < frames - fade; i++)
            peak = Math.Max(peak, Math.Abs(grain[i]));
        ((double)peak).ShouldBeGreaterThan(0.45 * 32768.0);

        // The grain comes from the proxy at the playhead. The full decoder does not seek or read for a grain.
        rig.Full.Seeks.ShouldBeEmpty();
        rig.Full.Reads.ShouldBe(0);
        for (int i = fade; i < frames - fade; i++)
            ((double)Math.Abs(grain[i] - ToneSample(2 * Rate + i))).ShouldBeLessThan(0.02 * 32768.0);
    }

    [Test]
    public void Grains_PlayWhenThePlayheadMoves_AfterTheGrainBefore()
    {
        using Rig rig = MakeRig();
        rig.Player.Seek(T(2.0), ESeekIntent.Scrub);
        rig.Player.Tick(0.0);
        rig.Buffers.Count.ShouldBe(1);

        // A move while the grain plays waits for its end. A tick with no move plays nothing more.
        rig.Player.Seek(T(2.1), ESeekIntent.Scrub);
        rig.Player.Tick(0.0);
        rig.Buffers.Count.ShouldBe(1);

        rig.Transport.FinishAll();
        rig.Player.Seek(T(2.1), ESeekIntent.Scrub);
        rig.Player.Tick(0.0);
        rig.Buffers.Count.ShouldBe(2);

        rig.Transport.FinishAll();
        rig.Player.Seek(T(2.1), ESeekIntent.Scrub);
        rig.Player.Tick(0.0);
        rig.Buffers.Count.ShouldBe(2);

        // A grain does not allocate.
        rig.Component.StreamingBufferEnqueuedShort -= rig.Capture;
        rig.Transport.FinishAll();
        rig.Player.Seek(T(2.2), ESeekIntent.Scrub);
        rig.Player.Tick(0.0);
        rig.Transport.FinishAll();
        long before = GC.GetAllocatedBytesForCurrentThread();
        rig.Player.Seek(T(2.3), ESeekIntent.Scrub);
        rig.Player.Tick(0.0);
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0L);
        foreach (FakeSource source in rig.Transport.Sources.Values)
            source.Playing.ShouldBeTrue();
    }

    [Test]
    public void FastReversePlayback_MakesNoSound_SlowReversePlaysGrains()
    {
        using Rig rig = MakeRig();
        rig.Player.Seek(T(3.0), ESeekIntent.Jump);
        rig.Player.SetRate(-AudioTrackEvaluator.SilentReverseRate);
        rig.Player.Play();
        rig.Player.Tick(0.0);
        rig.Buffers.Clear();
        for (int i = 0; i < 5; i++)
            rig.Player.Tick(0.05);
        rig.Buffers.ShouldBeEmpty();
        foreach (FakeSource source in rig.Transport.Sources.Values)
            source.Playing.ShouldBeFalse();

        rig.Player.SetRate(-1.0);
        rig.Player.Tick(0.05);
        rig.Buffers.Count.ShouldBe(1);
        rig.Buffers[0].Samples.Length.ShouldBe((int)Math.Round(AudioScrubGrainPlayer.GrainSeconds * Rate));
    }

    [Test]
    public void StaleProxy_GivesOneDiagnostic_AndSilenceUntilTheFullDecoderIsReady()
    {
        using Rig rig = MakeRig(staleProxy: true);
        rig.Player.Diagnostics.Count.ShouldBe(1);
        rig.Player.Diagnostics[0].Message.ShouldContain("does not match its source file");

        const long jump = Rate * 3 + 123;
        PlayAndJump(rig, jump);
        rig.Full.Seeks.ShouldBeEmpty();
        short[] proxyPart = Concatenate(rig.Buffers);
        proxyPart.ShouldAllBe(sample => sample == 0);

        // A scrub of a stale proxy plays no grain.
        rig.Scheduler.RunAll();
        for (int i = 0; i < 4; i++)
            rig.PlayOneBuffer();
        short[] samples = Concatenate(rig.Buffers);
        int change = 4 * Chunk;
        Math.Abs(samples[change]).ShouldBeLessThan((short)200);
        for (int n = change + 960; n < samples.Length; n++)
            samples[n].ShouldBe(ToneSample(jump + n), $"frame {n}");
        rig.Player.Diagnostics.Count.ShouldBe(1);

        rig.Player.Pause();
        rig.Buffers.Clear();
        rig.Player.Seek(T(1.0), ESeekIntent.Scrub);
        rig.Player.Tick(0.0);
        rig.Buffers.ShouldBeEmpty();
    }

    [Test]
    public void NoProxy_SeeksOnTheUpdateThread_AndScrubsInSilence()
    {
        using Rig rig = MakeRig(proxy: false);
        const long jump = Rate * 3 + 123;
        PlayAndJump(rig, jump);

        rig.Full.Seeks.ShouldBe([jump]);
        rig.Scheduler.Pending.ShouldBe(0);
        short[] samples = Concatenate(rig.Buffers);
        for (int n = 0; n < samples.Length; n++)
            samples[n].ShouldBe(ToneSample(jump + n));

        rig.Player.Pause();
        rig.Buffers.Clear();
        rig.Player.Seek(T(1.0), ESeekIntent.Scrub);
        rig.Player.Tick(0.0);
        rig.Buffers.ShouldBeEmpty();
        rig.Player.Diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void AudioBackend_ProxyIsT0_UpperLevelsOpenAndSeekTheFullDecoderNearTheDeadline()
    {
        (string directory, CutsceneAsset asset) = MakeAsset(proxy: true, staleProxy: false, clipStart: 1.0, clipOffset: 0.5);
        try
        {
            CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
            ManualScheduler scheduler = new();
            List<RecordingDecoder> opened = [];
            using AudioResidencyBackend backend = new(compiled, file =>
            {
                RecordingDecoder decoder = new(AudioTrackEvaluator.OpenDefaultDecoder(file));
                opened.Add(decoder);
                return decoder;
            }, scheduler);
            backend.SourceCount.ShouldBe(1);

            Guid id = CutsceneResidencyAssetKeys.ForAudioSource("tone.wav");
            CutsceneResidencyManifest manifest = backend.BuildManifest();
            manifest.Assets.ShouldBe([id]);
            manifest.AssetKinds.ShouldBe([EResidencyAssetKind.Audio]);

            CutsceneResidencyPlanner planner = new(manifest, backend);
            backend.AttachPlanner(planner);
            List<int> loaded = [];
            planner.LoadCompleted += (_, level) => loaded.Add(level);

            // T0 is the proxy, so it is resident at once.
            planner.Open();
            backend.GetLevel(id).ShouldBe(EResidencyLevel.Proxy);
            planner.Update(T(0), 1.0, null, 0.0);
            loaded.ShouldBe([0]);

            // T1 opens and seeks the full decoder at the clip start (deadline 1 s, file time 0.5 s), in the background.
            opened.ShouldBeEmpty();
            scheduler.RunAll().ShouldBe(1);
            opened.Count.ShouldBe(1);
            opened[0].Seeks.ShouldBe([Rate / 2L]);

            // The level is set before the planner applies the completion.
            backend.GetLevel(id).ShouldBe(EResidencyLevel.Coarse);
            backend.OpenDecoderCount.ShouldBe(1);
            planner.Update(T(0), 1.0, null, 0.0);
            loaded.ShouldBe([0, 1]);

            scheduler.RunAll().ShouldBe(1);
            planner.Update(T(0), 1.0, null, 0.0);
            loaded.ShouldBe([0, 1, 2]);
            backend.GetLevel(id).ShouldBe(EResidencyLevel.Target);

            // The T2 hold released the T1 ticket, and that cancel of a complete ticket closed its decoder.
            opened[0].Disposed.ShouldBeTrue();
            opened[1].Disposed.ShouldBeFalse();
            backend.OpenDecoderCount.ShouldBe(1);

            planner.Close();
            opened[1].Disposed.ShouldBeTrue();
            backend.GetLevel(id).ShouldBe(EResidencyLevel.None);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void AudioBackend_CancelOfRunningWork_ClosesTheDecoderAndReportsNothing()
    {
        (string directory, CutsceneAsset asset) = MakeAsset(proxy: true, staleProxy: false);
        try
        {
            CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
            ManualScheduler scheduler = new();
            List<RecordingDecoder> opened = [];
            ResidencyTicket cancelDuringOpen = ResidencyTicket.None;
            AudioResidencyBackend? owner = null;
            using AudioResidencyBackend backend = new(compiled, file =>
            {
                RecordingDecoder decoder = new(AudioTrackEvaluator.OpenDefaultDecoder(file));
                opened.Add(decoder);
                owner!.Cancel(cancelDuringOpen);
                return decoder;
            }, scheduler);
            owner = backend;
            Guid id = CutsceneResidencyAssetKeys.ForAudioSource("tone.wav");

            ResidencyTicket ticket = backend.Request(new ResidencyRequest(id, 2, T(2), 1.0f, 0));
            ticket.IsValid.ShouldBeTrue();
            backend.Cancel(ticket);
            scheduler.RunAll();
            opened.ShouldBeEmpty();
            backend.GetLevel(id).ShouldBe(EResidencyLevel.None);

            backend.Request(new ResidencyRequest(Guid.NewGuid(), 1, T(0), 1.0f, 0)).IsValid.ShouldBeFalse();
            backend.RejectedRequestCount.ShouldBe(1);

            // Work that runs when the cancel comes closes its decoder at its end and reports nothing.
            ResidencyTicket running = backend.Request(new ResidencyRequest(id, 1, T(2), 1.0f, 0));
            running.IsValid.ShouldBeTrue();
            cancelDuringOpen = running;
            scheduler.RunAll();
            opened.Count.ShouldBe(1);
            opened[0].Disposed.ShouldBeTrue();
            backend.GetLevel(id).ShouldBe(EResidencyLevel.None);
            backend.OpenDecoderCount.ShouldBe(0);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Session_RoutesAudioAssetsToTheAudioBackend()
    {
        (string directory, CutsceneAsset asset) = MakeAsset(proxy: true, staleProxy: false);
        try
        {
            SceneNode node = new("Speaker");
            asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
            Guid audioId = CutsceneResidencyAssetKeys.ForAudioSource("tone.wav");
            CutsceneResidencyManifestBuilder builder = new();
            builder.AddAsset(audioId, EResidencyAssetKind.Audio, asset.AudioProxies[0].EncodedData.Length);
            builder.AddAsset(Guid.NewGuid(), EResidencyAssetKind.Mesh, 100);
            builder.BeginBucket(T(0), T(6), startsAtCut: false);
            builder.AddEntry(1, 2, 0.5f);
            builder.EndBucket();
            asset.Manifest = builder.Build();
            asset.ManifestContentHash = CutsceneManifestHash.Compute(asset);

            CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
            using CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
            ManualScheduler scheduler = new();
            FakeResidencyBackend textures = new();
            List<CutsceneCompileDiagnostic> diagnostics = [];
            using CutsceneResidencySession session = CutsceneResidencySession.Create(
                compiled, bindings, textures, new NoAssets(), diagnostics,
                c => new AudioResidencyBackend(c, AudioTrackEvaluator.OpenDefaultDecoder, scheduler));

            session.ManifestState.ShouldBe(ECutsceneManifestState.Current);
            session.AudioBackend.ShouldBeOfType<AudioResidencyBackend>();
            session.AudioPlanner.ShouldNotBeNull();
            session.AudioPlanner!.Manifest.Assets.ShouldBe([audioId]);
            session.Manifest.Assets.ShouldBeEmpty();
            diagnostics.Count.ShouldBe(1);
            diagnostics[0].Message.ShouldBe("1 mesh assets of the residency manifest have no mesh residency, because no mesh asset of the manifest is a sub-mesh of the scene now. They keep the LODs that are resident now.");

            CutscenePlayer player = new(compiled, bindings, new CutsceneStateSnapshot(bindings));
            session.Open(player);
            session.AudioPlanner.IsOpen.ShouldBeTrue();
            session.AudioBackend!.GetLevel(audioId).ShouldBe(EResidencyLevel.Proxy);
            session.BeginTick();
            player.Tick(0.0);
            session.CompleteTick(player, 0.0);
            scheduler.Pending.ShouldBe(1);

            CutsceneResidencyPlanner audioPlanner = session.AudioPlanner;
            session.Close();
            audioPlanner.IsClosed.ShouldBeTrue();
            session.AudioPlanner.ShouldBeNull();
            scheduler.RunAll();

            // With no audio backend, the audio assets of the manifest give their own diagnostic.
            List<CutsceneCompileDiagnostic> noAudio = [];
            using CutsceneResidencySession withoutAudio = CutsceneResidencySession.Create(compiled, bindings, new FakeResidencyBackend(), new NoAssets(), noAudio, _ => null);
            withoutAudio.AudioPlanner.ShouldBeNull();
            noAudio.Count.ShouldBe(2);
            noAudio[1].Message.ShouldContain("1 audio assets of the residency manifest have no audio residency backend, because no audio residency backend was made");
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Worker_CloseDuringASeek_TheTaskClosesTheDecoder()
    {
        ManualScheduler scheduler = new();
        (string directory, _) = MakeAsset(proxy: false, staleProxy: false);
        try
        {
            RecordingDecoder decoder = new(AudioTrackEvaluator.OpenDefaultDecoder(Path.Combine(directory, "tone.wav")));
            AudioDecoderSeekWorker worker = new(decoder, scheduler);
            decoder.BeforeSeek = _ =>
            {
                worker.Close();
                decoder.Disposed.ShouldBeFalse();
            };

            worker.Request(100);
            scheduler.RunAll().ShouldBe(1);
            decoder.Seeks.ShouldBe([100L]);
            decoder.Disposed.ShouldBeTrue();

            // A closed worker starts no more seeks.
            worker.Request(200);
            scheduler.Pending.ShouldBe(0);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Worker_NewerRequestDuringASeek_SeeksAgainToTheNewestFrame()
    {
        ManualScheduler scheduler = new();
        (string directory, _) = MakeAsset(proxy: false, staleProxy: false);
        try
        {
            RecordingDecoder decoder = new(AudioTrackEvaluator.OpenDefaultDecoder(Path.Combine(directory, "tone.wav")));
            AudioDecoderSeekWorker worker = new(decoder, scheduler);
            bool idleDuringSeek = true;
            decoder.BeforeSeek = frame =>
            {
                if (frame != 100)
                    return;
                worker.Request(200);
                idleDuringSeek = worker.TryGetIdle(out _, out _);
            };

            worker.Request(100);
            scheduler.RunAll().ShouldBe(1);
            idleDuringSeek.ShouldBeFalse();
            decoder.Seeks.ShouldBe([100L, 200L]);
            worker.SeekCount.ShouldBe(2);
            worker.TryGetIdle(out long frame, out Exception? failure).ShouldBeTrue();
            frame.ShouldBe(200L);
            failure.ShouldBeNull();

            worker.Close();
            decoder.Disposed.ShouldBeTrue();
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Worker_GatedSeekOnARealThread_ClosesTheDecoderAtTheEndOfTheSeek()
    {
        (string directory, _) = MakeAsset(proxy: false, staleProxy: false);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim gate = new();
        RecordingDecoder decoder = new(AudioTrackEvaluator.OpenDefaultDecoder(Path.Combine(directory, "tone.wav")));
        AudioDecoderSeekWorker worker = new(decoder, TaskScheduler.Default);
        try
        {
            decoder.BeforeSeek = _ =>
            {
                entered.Set();
                gate.Wait(TimeSpan.FromSeconds(10));
            };

            worker.Request(100);
            entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            worker.TryGetIdle(out _, out _).ShouldBeFalse();

            // The seek still runs, so the close passes to the task.
            worker.Close();
            decoder.Disposed.ShouldBeFalse();
            gate.Set();
            SpinWait.SpinUntil(() => decoder.Disposed, TimeSpan.FromSeconds(10)).ShouldBeTrue();
        }
        finally
        {
            // A failed assert must not leave the seek blocked with the file open, or the delete hides the failure.
            gate.Set();
            worker.Close();
            SpinWait.SpinUntil(() => decoder.Disposed, TimeSpan.FromSeconds(10));
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Worker_UncaughtSeekError_ReportsIdleWithAFailure_AndAClosedWorkerDisposesInTheTask()
    {
        (string directory, _) = MakeAsset(proxy: false, staleProxy: false);
        try
        {
            // An error that the seek loop does not catch ends the task. The worker is then idle with a failure, so the
            // channel keeps the proxy, and a later close disposes the decoder at once.
            ManualScheduler scheduler = new();
            RecordingDecoder decoder = new(AudioTrackEvaluator.OpenDefaultDecoder(Path.Combine(directory, "tone.wav")));
            AudioDecoderSeekWorker worker = new(decoder, scheduler);
            decoder.BeforeSeek = _ => throw new OutOfMemoryException("The test seek ran out of memory.");
            worker.Request(100);
            scheduler.RunAll();
            worker.TryGetIdle(out _, out Exception? failure).ShouldBeTrue();
            failure.ShouldNotBeNull();
            decoder.Disposed.ShouldBeFalse();
            worker.Close();
            decoder.Disposed.ShouldBeTrue();

            // A close during the seek, before the error: the task disposes the decoder at its end.
            RecordingDecoder closing = new(AudioTrackEvaluator.OpenDefaultDecoder(Path.Combine(directory, "tone.wav")));
            AudioDecoderSeekWorker closedWorker = new(closing, scheduler);
            closing.BeforeSeek = _ =>
            {
                closedWorker.Close();
                closing.Disposed.ShouldBeFalse();
                throw new OutOfMemoryException("The test seek ran out of memory.");
            };
            closedWorker.Request(100);
            scheduler.RunAll();
            closing.Disposed.ShouldBeTrue();
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void AudioInputSignature_ChangesWhenASourceFileChangesOnDisk()
    {
        (string directory, CutsceneAsset asset) = MakeAsset(proxy: true, staleProxy: false);
        try
        {
            string before = AudioResidencyBackend.GetInputSignature(asset);
            AudioResidencyBackend.GetInputSignature(asset).ShouldBe(before);

            WriteToneWav(Path.Combine(directory, "tone.wav"), Rate * 5);
            AudioResidencyBackend.GetInputSignature(asset).ShouldNotBe(before);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void FailedBackgroundSeek_GivesOneDiagnostic_AndTheProxyKeepsPlaying()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.0);
        rig.Full.BeforeSeek = _ => throw new IOException("The test seek failed.");
        int readsAtJump = rig.Full.Reads;
        rig.Buffers.Clear();

        const long jump = Rate * 3 + 123;
        rig.Player.Seek(CutsceneTime.FromSamples(jump, Rate), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        rig.Scheduler.RunAll();
        for (int i = 0; i < 4; i++)
            rig.PlayOneBuffer();

        rig.Player.Diagnostics.Count.ShouldBe(1);
        rig.Player.Diagnostics[0].Message.ShouldContain("failed to seek in the background");
        rig.Player.Diagnostics[0].Message.ShouldContain("The test seek failed.");
        rig.Full.Reads.ShouldBe(readsAtJump);
        short[] samples = Concatenate(rig.Buffers);
        samples.Length.ShouldBe(8 * Chunk);
        for (int n = 0; n < samples.Length; n++)
            ((double)Math.Abs(samples[n] - ToneSample(jump + n))).ShouldBeLessThan(0.02 * 32768.0, $"frame {n}");
    }

    [Test]
    public void InvalidProxy_GivesOneDiagnostic_AndSilenceUntilTheFullDecoderIsReady()
    {
        using Rig rig = MakeRig(invalidProxy: true);
        rig.Player.Diagnostics.ShouldBeEmpty();

        const long jump = Rate * 3 + 123;
        PlayAndJump(rig, jump);
        rig.Player.Diagnostics.Count.ShouldBe(1);
        rig.Player.Diagnostics[0].Message.ShouldContain("does not open");
        Concatenate(rig.Buffers).ShouldAllBe(sample => sample == 0);

        rig.Scheduler.RunAll();
        for (int i = 0; i < 4; i++)
            rig.PlayOneBuffer();
        short[] samples = Concatenate(rig.Buffers);
        for (int n = 4 * Chunk + 960; n < samples.Length; n++)
            samples[n].ShouldBe(ToneSample(jump + n), $"frame {n}");
        rig.Player.Diagnostics.Count.ShouldBe(1);
    }

    [Test]
    public void JumpSeekWhilePaused_PlaysNoGrain()
    {
        using Rig rig = MakeRig();
        rig.Player.Seek(T(2.0), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        rig.Buffers.ShouldBeEmpty();

        // A scrub to the same frame did not move the playhead, so it plays nothing. A move plays a grain.
        rig.Player.Seek(T(2.0), ESeekIntent.Scrub);
        rig.Player.Tick(0.0);
        rig.Buffers.ShouldBeEmpty();
        rig.Player.Seek(T(2.1), ESeekIntent.Scrub);
        rig.Player.Tick(0.0);
        rig.Buffers.Count.ShouldBe(1);
    }

    [Test]
    public void AudioBackend_StaleProxy_IsNotResidentAtT0_ButItsSourceLoads()
    {
        (string directory, CutsceneAsset asset) = MakeAsset(proxy: true, staleProxy: true);
        try
        {
            CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
            ManualScheduler scheduler = new();
            using AudioResidencyBackend backend = new(compiled, AudioTrackEvaluator.OpenDefaultDecoder, scheduler);
            Guid id = CutsceneResidencyAssetKeys.ForAudioSource("tone.wav");

            backend.Request(new ResidencyRequest(id, 0, T(0), 1.0f, 0)).IsValid.ShouldBeFalse();
            backend.RejectedRequestCount.ShouldBe(1);
            backend.GetLevel(id).ShouldBe(EResidencyLevel.None);

            backend.Request(new ResidencyRequest(id, 1, T(1), 1.0f, 0)).IsValid.ShouldBeTrue();
            scheduler.RunAll();
            backend.GetLevel(id).ShouldBe(EResidencyLevel.Coarse);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Session_Refresh_KeepsTheAudioBackend_UntilTheAudioInputsChange()
    {
        (string directory, CutsceneAsset asset) = MakeAsset(proxy: true, staleProxy: false);
        try
        {
            SceneNode node = new("Speaker");
            asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
            CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
            using CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
            ManualScheduler scheduler = new();
            int created = 0;
            bool fail = false;
            List<CutsceneCompileDiagnostic> diagnostics = [];
            using CutsceneResidencySession session = CutsceneResidencySession.Create(
                compiled, bindings, new FakeResidencyBackend(), new NoAssets(), diagnostics,
                c =>
                {
                    created++;
                    return fail ? throw new InvalidOperationException("The test audio factory failed.") : new AudioResidencyBackend(c, AudioTrackEvaluator.OpenDefaultDecoder, scheduler);
                });
            IResidencyBackend? first = session.AudioBackend;
            first.ShouldNotBeNull();

            // A new compile with the same audio inputs keeps the audio backend.
            session.Refresh(CutsceneCompiler.Compile(asset, []), bindings, new NoAssets(), diagnostics);
            session.AudioBackend.ShouldBeSameAs(first);
            created.ShouldBe(1);

            // A changed clip makes a new audio part. A failure of it leaves the session as it was.
            ((AudioTrackClip)asset.Tracks[0].Clips[0]).ClipOffset = T(0.25);
            CompiledCutscene changed = CutsceneCompiler.Compile(asset, []);
            fail = true;
            CutsceneResidencyPlanner planner = session.Planner;
            Should.Throw<InvalidOperationException>(() => session.Refresh(changed, bindings, new NoAssets(), diagnostics));
            session.Planner.ShouldBeSameAs(planner);
            session.AudioBackend.ShouldBeSameAs(first);
            session.RefreshCount.ShouldBe(1);

            fail = false;
            session.Refresh(changed, bindings, new NoAssets(), diagnostics);
            session.AudioBackend.ShouldNotBeSameAs(first);
            session.AudioPlanner.ShouldNotBeNull();
            created.ShouldBe(3);
            scheduler.RunAll();
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void TierLead_CoversTheEstimatedSeekTime()
    {
        CutsceneAudioChannel.GetTierLeadChunks(0.0, Rate).ShouldBe(4);
        CutsceneAudioChannel.GetTierLeadChunks(0.0001, Rate).ShouldBe(4);

        // A cold Vorbis seek to 120 s is estimated at 0.24 s. Twice that is 23040 frames, 11 buffers after the first.
        CutsceneAudioChannel.GetTierLeadChunks(120.0 * CutsceneAudioChannel.VorbisSeekSecondsPerSecond, Rate).ShouldBe(15);
        CutsceneAudioChannel.GetTierLeadChunks(1000.0, Rate).ShouldBe(256);
    }

    [Test]
    public void SlowDecoder_FirstAttemptAfterAMeasuredSeek_MakesTheTierChange()
    {
        using Rig rig = MakeRig();
        rig.Player.Play();
        rig.Player.Tick(0.0);
        rig.Full.BeforeSeek = _ => Thread.Sleep(150);

        // The first seek of the decoder has no measure, so it gets the queue lead. It takes 150 ms and misses that lead.
        const long first = Rate * 3;
        rig.Player.Seek(CutsceneTime.FromSamples(first, Rate), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        rig.Scheduler.RunAll();
        rig.Full.Seeks.ShouldBe([first + 4L * Chunk]);

        // The next jump uses the measured time: twice 150 ms is 14400 frames, so the lead is at least 4 + 7 buffers.
        const long second = Rate * 1 + 77;
        rig.Player.Seek(CutsceneTime.FromSamples(second, Rate), ESeekIntent.Jump);
        rig.Player.Tick(0.0);
        rig.Scheduler.RunAll();
        rig.Full.Seeks.Count.ShouldBe(2);
        long lead = (rig.Full.Seeks[1] - second) / Chunk;
        (rig.Full.Seeks[1] - second).ShouldBe(lead * Chunk);
        lead.ShouldBeGreaterThanOrEqualTo(11L);
        lead.ShouldBeLessThanOrEqualTo(256L);

        // The seek was ready before its buffer, so the change happens at the first attempt, with no second request.
        int reads = rig.Full.Reads;
        rig.Buffers.Clear();
        for (int i = 0; i < lead - 4 + 2; i++)
            rig.PlayOneBuffer();
        rig.Full.Seeks.Count.ShouldBe(2);
        rig.Full.Reads.ShouldBeGreaterThan(reads);
        rig.Player.Diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void Restart_SeeksAndDecodesTheQueueBeforeItStopsTheSources()
    {
        using Rig rig = MakeRig(proxy: false);
        rig.Player.Play();
        rig.Player.Tick(0.0);
        int readsBefore = rig.Full.Reads;
        int stops = 0;
        int seeksAtStop = -1;
        int readsAtStop = -1;
        rig.Transport.BeforeStop = () =>
        {
            stops++;
            seeksAtStop = rig.Full.Seeks.Count;
            readsAtStop = rig.Full.Reads;
        };

        const long jump = Rate * 3 + 123;
        rig.Player.Seek(CutsceneTime.FromSamples(jump, Rate), ESeekIntent.Jump);
        rig.Player.Tick(0.0);

        // The stop comes after the seek and the decode of the 4 buffers, so the sources are stopped only to queue and play.
        stops.ShouldBe(1);
        seeksAtStop.ShouldBe(1);
        readsAtStop.ShouldBeGreaterThanOrEqualTo(readsBefore + 4);
        rig.Full.Reads.ShouldBe(readsAtStop);
        short[] samples = Concatenate(rig.Buffers.GetRange(rig.Buffers.Count - 4, 4));
        for (int n = 0; n < samples.Length; n++)
            samples[n].ShouldBe(ToneSample(jump + n));
    }
}
