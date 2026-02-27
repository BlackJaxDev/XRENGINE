using NUnit.Framework;
using Shouldly;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Audio;
using XREngine.Audio.Steam;

namespace XREngine.UnitTests.Audio
{
    /// <summary>
    /// Tests for Phase 6 — NAudio transport (managed mixer + NAudioTransport).
    /// All tests operate without a real audio device; the mixer's Read() is called
    /// directly to verify mixed output.
    /// </summary>
    [TestFixture]
    public sealed class NAudioTransportTests
    {
        #region Transport lifecycle

        [Test]
        public void NAudioTransport_Construction_DefaultState()
        {
            using var transport = new NAudioTransport();

            transport.SampleRate.ShouldBe(44100);
            transport.OutputChannels.ShouldBe(2);
            transport.IsOpen.ShouldBeFalse("Transport should not auto-open.");
            transport.DeviceName.ShouldBeNull();
        }

        [Test]
        public void NAudioTransport_Construction_CustomSampleRate()
        {
            using var transport = new NAudioTransport(sampleRate: 48000, channels: 1);

            transport.SampleRate.ShouldBe(48000);
            transport.OutputChannels.ShouldBe(1);
        }

        [Test]
        public void NAudioTransport_Dispose_Idempotent()
        {
            var transport = new NAudioTransport();
            transport.Dispose();
            transport.Dispose(); // Double dispose should not throw
        }

        [Test]
        public void NAudioTransport_CreateSource_AfterDispose_Throws()
        {
            var transport = new NAudioTransport();
            transport.Dispose();

            Should.Throw<ObjectDisposedException>(() => transport.CreateSource());
        }

        [Test]
        public void NAudioTransport_CreateBuffer_AfterDispose_Throws()
        {
            var transport = new NAudioTransport();
            transport.Dispose();

            Should.Throw<ObjectDisposedException>(() => transport.CreateBuffer());
        }

        #endregion

        #region Source lifecycle

        [Test]
        public void CreateSource_ReturnsValidHandle()
        {
            using var transport = new NAudioTransport();

            var handle = transport.CreateSource();

            handle.IsValid.ShouldBeTrue();
            handle.Id.ShouldBeGreaterThan(0u);
        }

        [Test]
        public void CreateSource_MultipleHandles_AreUnique()
        {
            using var transport = new NAudioTransport();

            var h1 = transport.CreateSource();
            var h2 = transport.CreateSource();
            var h3 = transport.CreateSource();

            h1.ShouldNotBe(h2);
            h2.ShouldNotBe(h3);
            h1.ShouldNotBe(h3);
        }

        [Test]
        public void DestroySource_InvalidHandle_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            transport.DestroySource(AudioSourceHandle.Invalid);
        }

        [Test]
        public void DestroySource_ValidHandle_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            var handle = transport.CreateSource();
            transport.DestroySource(handle);
            // Double destroy should also be safe
            transport.DestroySource(handle);
        }

        #endregion

        #region Buffer lifecycle

        [Test]
        public void CreateBuffer_ReturnsValidHandle()
        {
            using var transport = new NAudioTransport();

            var handle = transport.CreateBuffer();

            handle.IsValid.ShouldBeTrue();
            handle.Id.ShouldBeGreaterThan(0u);
        }

        [Test]
        public void CreateBuffer_MultipleHandles_AreUnique()
        {
            using var transport = new NAudioTransport();

            var b1 = transport.CreateBuffer();
            var b2 = transport.CreateBuffer();

            b1.ShouldNotBe(b2);
        }

        [Test]
        public void DestroyBuffer_InvalidHandle_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            transport.DestroyBuffer(AudioBufferHandle.Invalid);
        }

        [Test]
        public void UploadBufferData_Short16_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            var buf = transport.CreateBuffer();

            // 100 mono 16-bit samples at 44100Hz
            short[] pcm = new short[100];
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (short)(i * 100);

            transport.UploadBufferData(buf,
                MemoryMarshal.AsBytes(pcm.AsSpan()),
                44100, 1, SampleFormat.Short);
        }

        [Test]
        public void UploadBufferData_Float32_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            var buf = transport.CreateBuffer();

            float[] pcm = new float[100];
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = MathF.Sin(2 * MathF.PI * 440 * i / 44100f) * 0.5f;

            transport.UploadBufferData(buf,
                MemoryMarshal.AsBytes(pcm.AsSpan()),
                44100, 1, SampleFormat.Float);
        }

        [Test]
        public void UploadBufferData_Byte8_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            var buf = transport.CreateBuffer();

            byte[] pcm = new byte[100];
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (byte)(128 + i);

            transport.UploadBufferData(buf, pcm, 44100, 1, SampleFormat.Byte);
        }

        #endregion

        #region Play / Stop / Pause state

        [Test]
        public void Play_Source_IsSourcePlaying_ReturnsTrue()
        {
            using var transport = new NAudioTransport();

            var src = transport.CreateSource();
            var buf = transport.CreateBuffer();
            UploadSineWave(transport, buf, 44100, 1, 1.0f);
            transport.SetSourceBuffer(src, buf);

            transport.Play(src);

            transport.IsSourcePlaying(src).ShouldBeTrue();
        }

        [Test]
        public void Stop_Source_IsSourcePlaying_ReturnsFalse()
        {
            using var transport = new NAudioTransport();

            var src = transport.CreateSource();
            var buf = transport.CreateBuffer();
            UploadSineWave(transport, buf, 44100, 1, 1.0f);
            transport.SetSourceBuffer(src, buf);

            transport.Play(src);
            transport.Stop(src);

            transport.IsSourcePlaying(src).ShouldBeFalse();
        }

        [Test]
        public void Pause_Source_IsSourcePlaying_ReturnsFalse()
        {
            using var transport = new NAudioTransport();

            var src = transport.CreateSource();
            var buf = transport.CreateBuffer();
            UploadSineWave(transport, buf, 44100, 1, 1.0f);
            transport.SetSourceBuffer(src, buf);

            transport.Play(src);
            transport.Pause(src);

            transport.IsSourcePlaying(src).ShouldBeFalse();
        }

        [Test]
        public void Pause_ThenPlay_Resumes()
        {
            using var transport = new NAudioTransport();

            var src = transport.CreateSource();
            var buf = transport.CreateBuffer();
            UploadSineWave(transport, buf, 44100, 1, 1.0f);
            transport.SetSourceBuffer(src, buf);

            transport.Play(src);
            transport.Pause(src);
            transport.Play(src);

            transport.IsSourcePlaying(src).ShouldBeTrue();
        }

        [Test]
        public void IsSourcePlaying_NoBuffer_ReturnsFalse()
        {
            using var transport = new NAudioTransport();
            var src = transport.CreateSource();

            transport.IsSourcePlaying(src).ShouldBeFalse();
        }

        #endregion

        #region Source properties

        [Test]
        public void SetSourceGain_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            var src = transport.CreateSource();

            transport.SetSourceGain(src, 0.5f);
            transport.SetSourceGain(src, 0.0f);
            transport.SetSourceGain(src, 1.0f);
        }

        [Test]
        public void SetSourcePitch_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            var src = transport.CreateSource();

            transport.SetSourcePitch(src, 0.5f);
            transport.SetSourcePitch(src, 2.0f);
        }

        [Test]
        public void SetSourceLooping_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            var src = transport.CreateSource();

            transport.SetSourceLooping(src, true);
            transport.SetSourceLooping(src, false);
        }

        [Test]
        public void SetSourcePosition_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            var src = transport.CreateSource();

            transport.SetSourcePosition(src, new Vector3(5, 0, 0));
        }

        [Test]
        public void SetSourceVelocity_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            var src = transport.CreateSource();

            transport.SetSourceVelocity(src, new Vector3(0, 0, 10));
        }

        #endregion

        #region Listener properties

        [Test]
        public void SetListenerPosition_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            transport.SetListenerPosition(Vector3.Zero);
            transport.SetListenerPosition(new Vector3(1, 2, 3));
        }

        [Test]
        public void SetListenerVelocity_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            transport.SetListenerVelocity(Vector3.Zero);
        }

        [Test]
        public void SetListenerOrientation_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            transport.SetListenerOrientation(-Vector3.UnitZ, Vector3.UnitY);
        }

        [Test]
        public void SetListenerGain_DoesNotThrow()
        {
            using var transport = new NAudioTransport();
            transport.SetListenerGain(0.5f);
            transport.SetListenerGain(1.0f);
        }

        #endregion

        #region Capture (deferred)

        [Test]
        public void OpenCaptureDevice_ReturnsNull()
        {
            using var transport = new NAudioTransport();
            var device = transport.OpenCaptureDevice(null, 44100, SampleFormat.Short, 4096);
            device.ShouldBeNull("NAudio capture is deferred to a later phase.");
        }

        #endregion

        #region Mixer output verification

        [Test]
        public void Mixer_NoSources_OutputIsSilence()
        {
            using var transport = new NAudioTransport(44100, 2);
            var mixer = transport.Mixer;

            float[] output = new float[256];
            mixer.Read(output, 0, output.Length);

            for (int i = 0; i < output.Length; i++)
                output[i].ShouldBe(0f, $"Sample {i} should be zero (silence).");
        }

        [Test]
        public void Mixer_PlayingSource_ProducesNonZeroOutput()
        {
            using var transport = new NAudioTransport(44100, 1);

            var src = transport.CreateSource();
            var buf = transport.CreateBuffer();
            UploadSineWave(transport, buf, 44100, 1, 0.5f);
            transport.SetSourceBuffer(src, buf);
            transport.Play(src);

            float[] output = new float[256];
            transport.Mixer.Read(output, 0, output.Length);

            bool anyNonZero = false;
            for (int i = 0; i < output.Length; i++)
            {
                if (MathF.Abs(output[i]) > 1e-8f)
                {
                    anyNonZero = true;
                    break;
                }
            }
            anyNonZero.ShouldBeTrue("Playing source with sine data should produce non-zero output.");
        }

        [Test]
        public void Mixer_StoppedSource_OutputIsSilence()
        {
            using var transport = new NAudioTransport(44100, 1);

            var src = transport.CreateSource();
            var buf = transport.CreateBuffer();
            UploadSineWave(transport, buf, 44100, 1, 0.5f);
            transport.SetSourceBuffer(src, buf);

            // Source is stopped — should produce silence
            float[] output = new float[256];
            transport.Mixer.Read(output, 0, output.Length);

            for (int i = 0; i < output.Length; i++)
                output[i].ShouldBe(0f, $"Stopped source should produce silence (sample {i}).");
        }

        [Test]
        public void Mixer_SourceGain_ScalesOutput()
        {
            using var transport = new NAudioTransport(44100, 1);

            var src = transport.CreateSource();
            var buf = transport.CreateBuffer();

            // Upload a constant-value buffer (all 1.0)
            float[] ones = new float[256];
            Array.Fill(ones, 1.0f);
            transport.UploadBufferData(buf,
                MemoryMarshal.AsBytes(ones.AsSpan()),
                44100, 1, SampleFormat.Float);
            transport.SetSourceBuffer(src, buf);
            transport.SetSourceGain(src, 0.5f);
            transport.Play(src);

            float[] output = new float[256];
            transport.Mixer.Read(output, 0, output.Length);

            // Source gain 0.5 × sample 1.0 × listener gain 1.0 = 0.5
            for (int i = 0; i < output.Length; i++)
                output[i].ShouldBe(0.5f, 1e-6f, $"Sample {i} should be scaled by source gain.");
        }

        [Test]
        public void Mixer_ListenerGain_ScalesOutput()
        {
            using var transport = new NAudioTransport(44100, 1);

            var src = transport.CreateSource();
            var buf = transport.CreateBuffer();

            float[] ones = new float[256];
            Array.Fill(ones, 1.0f);
            transport.UploadBufferData(buf,
                MemoryMarshal.AsBytes(ones.AsSpan()),
                44100, 1, SampleFormat.Float);
            transport.SetSourceBuffer(src, buf);
            transport.SetListenerGain(0.25f);
            transport.Play(src);

            float[] output = new float[256];
            transport.Mixer.Read(output, 0, output.Length);

            // Source gain 1.0 × sample 1.0 × listener gain 0.25 = 0.25
            for (int i = 0; i < output.Length; i++)
                output[i].ShouldBe(0.25f, 1e-6f, $"Sample {i} should be scaled by listener gain.");
        }

        [Test]
        public void Mixer_MonoToStereo_DuplicatesChannels()
        {
            using var transport = new NAudioTransport(44100, 2);

            var src = transport.CreateSource();
            var buf = transport.CreateBuffer();

            // Upload mono buffer: all 0.75
            float[] mono = new float[128];
            Array.Fill(mono, 0.75f);
            transport.UploadBufferData(buf,
                MemoryMarshal.AsBytes(mono.AsSpan()),
                44100, 1, SampleFormat.Float);
            transport.SetSourceBuffer(src, buf);
            transport.Play(src);

            // Read stereo output (128 frames × 2 channels = 256 samples)
            float[] output = new float[256];
            transport.Mixer.Read(output, 0, output.Length);

            // Each frame should have the mono value in both L and R
            for (int frame = 0; frame < 128; frame++)
            {
                output[frame * 2].ShouldBe(0.75f, 1e-6f, $"Left channel frame {frame}");
                output[frame * 2 + 1].ShouldBe(0.75f, 1e-6f, $"Right channel frame {frame}");
            }
        }

        [Test]
        public void Mixer_TwoSources_MixedAdditive()
        {
            using var transport = new NAudioTransport(44100, 1);

            var src1 = transport.CreateSource();
            var src2 = transport.CreateSource();
            var buf1 = transport.CreateBuffer();
            var buf2 = transport.CreateBuffer();

            // Buffer 1: constant 0.3
            float[] data1 = new float[128];
            Array.Fill(data1, 0.3f);
            transport.UploadBufferData(buf1,
                MemoryMarshal.AsBytes(data1.AsSpan()),
                44100, 1, SampleFormat.Float);

            // Buffer 2: constant 0.4
            float[] data2 = new float[128];
            Array.Fill(data2, 0.4f);
            transport.UploadBufferData(buf2,
                MemoryMarshal.AsBytes(data2.AsSpan()),
                44100, 1, SampleFormat.Float);

            transport.SetSourceBuffer(src1, buf1);
            transport.SetSourceBuffer(src2, buf2);
            transport.Play(src1);
            transport.Play(src2);

            float[] output = new float[128];
            transport.Mixer.Read(output, 0, output.Length);

            // 0.3 + 0.4 = 0.7
            for (int i = 0; i < output.Length; i++)
                output[i].ShouldBe(0.7f, 1e-5f, $"Sample {i} should be additive mix of both sources.");
        }

        [Test]
        public void Mixer_Looping_Source_WrapsAround()
        {
            using var transport = new NAudioTransport(44100, 1);

            var src = transport.CreateSource();
            var buf = transport.CreateBuffer();

            // Upload a tiny buffer (4 samples)
            float[] data = [0.1f, 0.2f, 0.3f, 0.4f];
            transport.UploadBufferData(buf,
                MemoryMarshal.AsBytes(data.AsSpan()),
                44100, 1, SampleFormat.Float);

            transport.SetSourceBuffer(src, buf);
            transport.SetSourceLooping(src, true);
            transport.Play(src);

            // Read 12 samples (3 loops of 4)
            float[] output = new float[12];
            transport.Mixer.Read(output, 0, output.Length);

            // Should repeat: 0.1, 0.2, 0.3, 0.4, 0.1, 0.2, 0.3, 0.4, 0.1, 0.2, 0.3, 0.4
            for (int i = 0; i < 12; i++)
                output[i].ShouldBe(data[i % 4], 1e-6f, $"Sample {i} should be looped correctly.");
        }

        [Test]
        public void Mixer_NonLooping_Source_StopsAtEnd()
        {
            using var transport = new NAudioTransport(44100, 1);

            var src = transport.CreateSource();
            var buf = transport.CreateBuffer();

            // Upload 4-sample buffer
            float[] data = [0.1f, 0.2f, 0.3f, 0.4f];
            transport.UploadBufferData(buf,
                MemoryMarshal.AsBytes(data.AsSpan()),
                44100, 1, SampleFormat.Float);

            transport.SetSourceBuffer(src, buf);
            transport.SetSourceLooping(src, false);
            transport.Play(src);

            // Read 8 samples — last 4 should be silence
            float[] output = new float[8];
            transport.Mixer.Read(output, 0, output.Length);

            output[0].ShouldBe(0.1f, 1e-6f);
            output[1].ShouldBe(0.2f, 1e-6f);
            output[2].ShouldBe(0.3f, 1e-6f);
            output[3].ShouldBe(0.4f, 1e-6f);
            // Source should have stopped, remaining is silence
            output[4].ShouldBe(0f, 1e-6f);
            output[5].ShouldBe(0f, 1e-6f);
            output[6].ShouldBe(0f, 1e-6f);
            output[7].ShouldBe(0f, 1e-6f);

            transport.IsSourcePlaying(src).ShouldBeFalse("Source should auto-stop at end of buffer.");
        }

        [Test]
        public void Mixer_Pitch_AffectsPlaybackRate()
        {
            using var transport = new NAudioTransport(44100, 1);

            var src = transport.CreateSource();
            var buf = transport.CreateBuffer();

            // Upload 8-sample buffer with distinct ascending values
            float[] data = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f];
            transport.UploadBufferData(buf,
                MemoryMarshal.AsBytes(data.AsSpan()),
                44100, 1, SampleFormat.Float);

            transport.SetSourceBuffer(src, buf);
            transport.SetSourcePitch(src, 2.0f); // Double speed
            transport.Play(src);

            // At pitch 2.0, 4 output frames consume 8 buffer frames (skipping every other)
            float[] output = new float[4];
            transport.Mixer.Read(output, 0, output.Length);

            // Position advances: 0, 2, 4, 6 → samples at index 0, 2, 4, 6
            output[0].ShouldBe(0.1f, 1e-6f, "Frame 0 → buffer[0]");
            output[1].ShouldBe(0.3f, 1e-6f, "Frame 1 → buffer[2]");
            output[2].ShouldBe(0.5f, 1e-6f, "Frame 2 → buffer[4]");
            output[3].ShouldBe(0.7f, 1e-6f, "Frame 3 → buffer[6]");
        }

        #endregion

        #region Buffer queue / streaming

        [Test]
        public void QueueBuffers_StreamingPlayback()
        {
            using var transport = new NAudioTransport(44100, 1);

            var src = transport.CreateSource();
            var buf1 = transport.CreateBuffer();
            var buf2 = transport.CreateBuffer();

            // Buffer 1: 4 samples of 0.1
            float[] data1 = [0.1f, 0.1f, 0.1f, 0.1f];
            transport.UploadBufferData(buf1,
                MemoryMarshal.AsBytes(data1.AsSpan()),
                44100, 1, SampleFormat.Float);

            // Buffer 2: 4 samples of 0.9
            float[] data2 = [0.9f, 0.9f, 0.9f, 0.9f];
            transport.UploadBufferData(buf2,
                MemoryMarshal.AsBytes(data2.AsSpan()),
                44100, 1, SampleFormat.Float);

            // Queue both buffers
            Span<AudioBufferHandle> bufs = [buf1, buf2];
            transport.QueueBuffers(src, bufs);
            transport.Play(src);

            // Read 8 samples: first 4 from buf1, next 4 from buf2
            float[] output = new float[8];
            transport.Mixer.Read(output, 0, output.Length);

            for (int i = 0; i < 4; i++)
                output[i].ShouldBe(0.1f, 1e-6f, $"First buffer sample {i}");
            for (int i = 4; i < 8; i++)
                output[i].ShouldBe(0.9f, 1e-6f, $"Second buffer sample {i}");
        }

        [Test]
        public void UnqueueProcessedBuffers_ReturnsProcessedAfterPlayback()
        {
            using var transport = new NAudioTransport(44100, 1);

            var src = transport.CreateSource();
            var buf1 = transport.CreateBuffer();
            var buf2 = transport.CreateBuffer();

            // Buffer 1: 4 samples
            float[] data1 = [0.1f, 0.1f, 0.1f, 0.1f];
            transport.UploadBufferData(buf1,
                MemoryMarshal.AsBytes(data1.AsSpan()),
                44100, 1, SampleFormat.Float);

            // Buffer 2: 4 samples
            float[] data2 = [0.2f, 0.2f, 0.2f, 0.2f];
            transport.UploadBufferData(buf2,
                MemoryMarshal.AsBytes(data2.AsSpan()),
                44100, 1, SampleFormat.Float);

            Span<AudioBufferHandle> bufs = [buf1, buf2];
            transport.QueueBuffers(src, bufs);
            transport.Play(src);

            // Read 8 samples — consumes both buffers, buf1 becomes processed
            float[] output = new float[8];
            transport.Mixer.Read(output, 0, output.Length);

            // Unqueue processed buffers
            Span<AudioBufferHandle> processed = stackalloc AudioBufferHandle[4];
            int count = transport.UnqueueProcessedBuffers(src, processed);

            // buf1 should be processed (it was fully played and we advanced to buf2)
            count.ShouldBeGreaterThanOrEqualTo(1, "At least buf1 should be processed.");
            processed[0].ShouldBe(buf1, "First processed buffer should be buf1.");
        }

        [Test]
        public void UnqueueProcessedBuffers_NoProcessed_ReturnsZero()
        {
            using var transport = new NAudioTransport(44100, 1);

            var src = transport.CreateSource();

            Span<AudioBufferHandle> processed = stackalloc AudioBufferHandle[4];
            int count = transport.UnqueueProcessedBuffers(src, processed);

            count.ShouldBe(0);
        }

        #endregion

        #region PCM conversion

        [Test]
        public void ConvertToFloat_Short16_Normalized()
        {
            short[] pcm = [0, short.MaxValue, short.MinValue];
            var result = NAudioMixer.ConvertToFloat(MemoryMarshal.AsBytes(pcm.AsSpan()), SampleFormat.Short);

            result.Length.ShouldBe(3);
            result[0].ShouldBe(0f, 1e-4f, "Zero");
            result[1].ShouldBe(32767f / 32768f, 1e-4f, "Max positive");
            result[2].ShouldBe(-1.0f, 1e-4f, "Min negative");
        }

        [Test]
        public void ConvertToFloat_Float32_PassThrough()
        {
            float[] pcm = [0.0f, 0.5f, -0.75f, 1.0f];
            var result = NAudioMixer.ConvertToFloat(MemoryMarshal.AsBytes(pcm.AsSpan()), SampleFormat.Float);

            result.Length.ShouldBe(4);
            for (int i = 0; i < pcm.Length; i++)
                result[i].ShouldBe(pcm[i], 1e-6f);
        }

        [Test]
        public void ConvertToFloat_Byte8_Normalized()
        {
            byte[] pcm = [128, 255, 0]; // 128 = silence, 255 ≈ +1, 0 ≈ -1
            var result = NAudioMixer.ConvertToFloat(pcm, SampleFormat.Byte);

            result.Length.ShouldBe(3);
            result[0].ShouldBe(0f, 1e-2f, "128 → ~0 (silence)");
            result[1].ShouldBeGreaterThan(0.9f, "255 → near +1");
            result[2].ShouldBeLessThan(-0.9f, "0 → near -1");
        }

        #endregion

        #region AudioManager NAudio combo (V2 integration)

        [Test]
        public void AudioManager_NAudio_Passthrough_CreatesNAudioTransport()
        {
            bool prev = AudioSettings.AudioArchitectureV2;
            try
            {
                AudioSettings.AudioArchitectureV2 = true;
                var manager = new AudioManager
                {
                    DefaultTransport = AudioTransportType.NAudio,
                    DefaultEffects = AudioEffectsType.Passthrough,
                };

                using var listener = manager.NewListener("test-naudio-passthrough");

                listener.IsV2.ShouldBeTrue();
                listener.Transport.ShouldNotBeNull();
                listener.Transport.ShouldBeOfType<NAudioTransport>();
                listener.EffectsProcessor.ShouldNotBeNull();
                listener.EffectsProcessor.ShouldBeOfType<PassthroughProcessor>();
            }
            finally
            {
                AudioSettings.AudioArchitectureV2 = prev;
            }
        }

        [Test]
        public void AudioManager_NAudio_SteamAudio_CreatesNAudioTransportWithEffects()
        {
            bool prev = AudioSettings.AudioArchitectureV2;
            try
            {
                AudioSettings.AudioArchitectureV2 = true;
                var manager = new AudioManager
                {
                    DefaultTransport = AudioTransportType.NAudio,
                    DefaultEffects = AudioEffectsType.SteamAudio,
                };

                using var listener = manager.NewListener("test-naudio-steamaudio");

                listener.IsV2.ShouldBeTrue();
                listener.Transport.ShouldBeOfType<NAudioTransport>();
                // Either SteamAudioProcessor (if phonon.dll present) or PassthroughProcessor fallback
                (listener.EffectsProcessor is SteamAudioProcessor
                 || listener.EffectsProcessor is PassthroughProcessor)
                    .ShouldBeTrue("Should be SteamAudioProcessor or PassthroughProcessor fallback.");
            }
            finally
            {
                AudioSettings.AudioArchitectureV2 = prev;
            }
        }

        [Test]
        public void AudioManager_NAudio_EFX_AutoCorrects_ToPassthrough()
        {
            bool prev = AudioSettings.AudioArchitectureV2;
            try
            {
                AudioSettings.AudioArchitectureV2 = true;
                var manager = new AudioManager
                {
                    DefaultTransport = AudioTransportType.NAudio,
                    DefaultEffects = AudioEffectsType.OpenAL_EFX,
                };

                using var listener = manager.NewListener("test-naudio-efx-fallback");

                listener.IsV2.ShouldBeTrue();
                listener.Transport.ShouldBeOfType<NAudioTransport>();
                // EFX requires OpenAL — ValidateCombo auto-corrects to Passthrough
                listener.EffectsProcessor.ShouldBeOfType<PassthroughProcessor>();
            }
            finally
            {
                AudioSettings.AudioArchitectureV2 = prev;
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Uploads a mono 440Hz sine wave into the given buffer.
        /// </summary>
        private static void UploadSineWave(
            NAudioTransport transport,
            AudioBufferHandle buffer,
            int sampleRate,
            int channels,
            float amplitude,
            int sampleCount = 4410) // 100ms at 44100Hz
        {
            float[] pcm = new float[sampleCount * channels];
            for (int i = 0; i < sampleCount; i++)
            {
                float sample = MathF.Sin(2 * MathF.PI * 440 * i / sampleRate) * amplitude;
                for (int ch = 0; ch < channels; ch++)
                    pcm[i * channels + ch] = sample;
            }

            transport.UploadBufferData(buffer,
                MemoryMarshal.AsBytes(pcm.AsSpan()),
                sampleRate, channels, SampleFormat.Float);
        }

        #endregion
    }
}
