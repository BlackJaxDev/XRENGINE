using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Audio;
using XREngine.Audio.Steam;

namespace XREngine.UnitTests.Audio
{
    /// <summary>
    /// Tests for Phase 2 (PassthroughProcessor + combo validation) and
    /// Phase 3 (SteamAudioProcessor) of the audio architecture migration.
    /// </summary>
    [TestFixture]
    public sealed class AudioProcessorTests
    {
        #region PassthroughProcessor

        [Test]
        public void PassthroughProcessor_ProcessBuffer_CopiesInputToOutput()
        {
            using var processor = new PassthroughProcessor();
            processor.Initialize(new AudioEffectsSettings { SampleRate = 44100, FrameSize = 1024 });

            var handle = processor.AddSource(new AudioEffectsSourceSettings());

            Span<float> input = stackalloc float[8];
            Span<float> output = stackalloc float[8];
            for (int i = 0; i < input.Length; i++)
                input[i] = i * 0.1f;

            processor.ProcessBuffer(handle, input, output, 1, 44100);

            for (int i = 0; i < input.Length; i++)
                output[i].ShouldBe(input[i], 1e-6f, $"Sample {i} mismatch");
        }

        [Test]
        public void PassthroughProcessor_ProcessBuffer_EmptySpans_NoThrow()
        {
            using var processor = new PassthroughProcessor();
            processor.Initialize(new AudioEffectsSettings());

            var handle = processor.AddSource(new AudioEffectsSourceSettings());

            // Empty input and output — should not throw.
            processor.ProcessBuffer(handle, ReadOnlySpan<float>.Empty, Span<float>.Empty, 1, 44100);
        }

        [Test]
        public void PassthroughProcessor_Capabilities_AllFalse()
        {
            using var processor = new PassthroughProcessor();

            processor.SupportsHRTF.ShouldBeFalse();
            processor.SupportsOcclusion.ShouldBeFalse();
            processor.SupportsReflections.ShouldBeFalse();
            processor.SupportsPathing.ShouldBeFalse();
            processor.SupportsSceneGeometry.ShouldBeFalse();
        }

        [Test]
        public void PassthroughProcessor_AddSource_ReturnsValidHandle()
        {
            using var processor = new PassthroughProcessor();
            processor.Initialize(new AudioEffectsSettings());

            var h1 = processor.AddSource(new AudioEffectsSourceSettings());
            var h2 = processor.AddSource(new AudioEffectsSourceSettings());

            h1.IsValid.ShouldBeTrue();
            h2.IsValid.ShouldBeTrue();
            h1.ShouldNotBe(h2, "Each source should get a unique handle.");
        }

        [Test]
        public void PassthroughProcessor_RemoveSource_DoesNotThrow()
        {
            using var processor = new PassthroughProcessor();
            processor.Initialize(new AudioEffectsSettings());

            var handle = processor.AddSource(new AudioEffectsSourceSettings());
            processor.RemoveSource(handle);

            // Removing an already-removed handle should also not throw
            processor.RemoveSource(handle);
        }

        [Test]
        public void PassthroughProcessor_LifecycleMethods_DoNotThrow()
        {
            using var processor = new PassthroughProcessor();

            // Initialize, tick, listener pose, scene — all no-ops.
            processor.Initialize(new AudioEffectsSettings { SampleRate = 48000, FrameSize = 512 });
            processor.Tick(0.016f);
            processor.SetListenerPose(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
            processor.SetScene(null);
            processor.Shutdown();
        }

        #endregion

        #region AudioManager Combo Validation

        [Test]
        public void ValidateCombo_OpenAL_EFX_IsValid()
        {
            var (transport, effects) = AudioManager.ValidateCombo(
                AudioTransportType.OpenAL, AudioEffectsType.OpenAL_EFX);

            transport.ShouldBe(AudioTransportType.OpenAL);
            effects.ShouldBe(AudioEffectsType.OpenAL_EFX);
        }

        [Test]
        public void ValidateCombo_OpenAL_Passthrough_IsValid()
        {
            var (transport, effects) = AudioManager.ValidateCombo(
                AudioTransportType.OpenAL, AudioEffectsType.Passthrough);

            transport.ShouldBe(AudioTransportType.OpenAL);
            effects.ShouldBe(AudioEffectsType.Passthrough);
        }

        [Test]
        public void ValidateCombo_OpenAL_SteamAudio_IsValid()
        {
            var (transport, effects) = AudioManager.ValidateCombo(
                AudioTransportType.OpenAL, AudioEffectsType.SteamAudio);

            transport.ShouldBe(AudioTransportType.OpenAL);
            effects.ShouldBe(AudioEffectsType.SteamAudio);
        }

        [Test]
        public void ValidateCombo_NAudio_EFX_AutoCorrected_ToPassthrough()
        {
            // NAudio + EFX is invalid (EFX requires OpenAL context).
            var (transport, effects) = AudioManager.ValidateCombo(
                AudioTransportType.NAudio, AudioEffectsType.OpenAL_EFX);

            transport.ShouldBe(AudioTransportType.NAudio);
            effects.ShouldBe(AudioEffectsType.Passthrough,
                "EFX requires OpenAL transport — combo validation should auto-correct to Passthrough.");
        }

        [Test]
        public void ValidateCombo_NAudio_Passthrough_IsValid()
        {
            var (transport, effects) = AudioManager.ValidateCombo(
                AudioTransportType.NAudio, AudioEffectsType.Passthrough);

            transport.ShouldBe(AudioTransportType.NAudio);
            effects.ShouldBe(AudioEffectsType.Passthrough);
        }

        [Test]
        public void ValidateCombo_NAudio_SteamAudio_IsValid()
        {
            var (transport, effects) = AudioManager.ValidateCombo(
                AudioTransportType.NAudio, AudioEffectsType.SteamAudio);

            transport.ShouldBe(AudioTransportType.NAudio);
            effects.ShouldBe(AudioEffectsType.SteamAudio);
        }

        #endregion

        #region AudioManager V2 Listener Creation

        [Test]
        public void NewListener_V2_OpenAL_Passthrough_CreatesPassthroughProcessor()
        {
            bool prev = AudioSettings.AudioArchitectureV2;
            try
            {
                AudioSettings.AudioArchitectureV2 = true;
                var manager = new AudioManager
                {
                    DefaultTransport = AudioTransportType.OpenAL,
                    DefaultEffects = AudioEffectsType.Passthrough,
                };

                using var listener = manager.NewListener("test-passthrough");

                listener.IsV2.ShouldBeTrue();
                listener.Transport.ShouldNotBeNull();
                listener.Transport.ShouldBeOfType<OpenALTransport>();
                listener.EffectsProcessor.ShouldNotBeNull();
                listener.EffectsProcessor.ShouldBeOfType<PassthroughProcessor>();
            }
            finally
            {
                AudioSettings.AudioArchitectureV2 = prev;
            }
        }

        [Test]
        public void NewListener_V2_OpenAL_EFX_CreatesEfxProcessor()
        {
            bool prev = AudioSettings.AudioArchitectureV2;
            try
            {
                AudioSettings.AudioArchitectureV2 = true;
                var manager = new AudioManager
                {
                    DefaultTransport = AudioTransportType.OpenAL,
                    DefaultEffects = AudioEffectsType.OpenAL_EFX,
                };

                using var listener = manager.NewListener("test-efx");

                listener.IsV2.ShouldBeTrue();
                listener.Transport.ShouldBeOfType<OpenALTransport>();
                listener.EffectsProcessor.ShouldBeOfType<OpenALEfxProcessor>();
            }
            catch (Exception ex) when (ex.Message.Contains("OpenAL", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Inconclusive($"OpenAL device unavailable — skipping: {ex.Message}");
            }
            finally
            {
                AudioSettings.AudioArchitectureV2 = prev;
            }
        }

        [Test]
        public void NewListener_V2_SteamAudio_FallsBackWhenDllMissing()
        {
            // If phonon.dll is not present, SteamAudioProcessor construction will fail.
            // AudioManager should gracefully fall back to PassthroughProcessor.
            bool prev = AudioSettings.AudioArchitectureV2;
            try
            {
                AudioSettings.AudioArchitectureV2 = true;
                var manager = new AudioManager
                {
                    DefaultTransport = AudioTransportType.OpenAL,
                    DefaultEffects = AudioEffectsType.SteamAudio,
                };

                using var listener = manager.NewListener("test-steamaudio-fallback");

                listener.IsV2.ShouldBeTrue();
                // Either SteamAudioProcessor (if phonon.dll present) or PassthroughProcessor (fallback)
                listener.EffectsProcessor.ShouldNotBeNull();
                (listener.EffectsProcessor is SteamAudioProcessor || listener.EffectsProcessor is PassthroughProcessor)
                    .ShouldBeTrue("Should be SteamAudioProcessor or PassthroughProcessor fallback.");
            }
            catch (Exception ex) when (ex.Message.Contains("OpenAL", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Inconclusive($"OpenAL device unavailable — skipping: {ex.Message}");
            }
            finally
            {
                AudioSettings.AudioArchitectureV2 = prev;
            }
        }

        #endregion

        #region SteamAudioProcessor (conditional on phonon.dll)

        [Test]
        public void SteamAudioProcessor_Capabilities_ReportsHRTFAndOcclusion()
        {
            SteamAudioProcessor? processor = null;
            try
            {
                processor = new SteamAudioProcessor();
                processor.Initialize(new AudioEffectsSettings { SampleRate = 44100, FrameSize = 1024 });
            }
            catch (Exception ex) when (ex.Message.Contains("phonon", StringComparison.OrdinalIgnoreCase)
                                    || ex is DllNotFoundException
                                    || ex is InvalidOperationException)
            {
                processor?.Dispose();
                Assert.Inconclusive($"Steam Audio (phonon.dll) unavailable — skipping: {ex.Message}");
                return;
            }

            using (processor)
            {
                processor.SupportsHRTF.ShouldBeTrue();
                processor.SupportsOcclusion.ShouldBeTrue();
                processor.SupportsReflections.ShouldBeFalse("Reflections are Phase 5.");
                processor.SupportsPathing.ShouldBeFalse("Pathing is Phase 5.");
                processor.SupportsSceneGeometry.ShouldBeTrue();
            }
        }

        [Test]
        public void SteamAudioProcessor_AddRemoveSource_Works()
        {
            SteamAudioProcessor? processor = null;
            try
            {
                processor = new SteamAudioProcessor();
                processor.Initialize(new AudioEffectsSettings { SampleRate = 44100, FrameSize = 1024 });
            }
            catch (Exception ex) when (ex.Message.Contains("phonon", StringComparison.OrdinalIgnoreCase)
                                    || ex is DllNotFoundException
                                    || ex is InvalidOperationException)
            {
                processor?.Dispose();
                Assert.Inconclusive($"Steam Audio unavailable — skipping: {ex.Message}");
                return;
            }

            using (processor)
            {
                var h = processor.AddSource(new AudioEffectsSourceSettings
                {
                    Position = new Vector3(5, 0, 0),
                    Forward = -Vector3.UnitZ,
                });
                h.IsValid.ShouldBeTrue();

                // RemoveSource should not throw
                processor.RemoveSource(h);

                // Removing again should not throw either
                processor.RemoveSource(h);
            }
        }

        [Test]
        public void SteamAudioProcessor_SetPose_DoesNotThrow()
        {
            SteamAudioProcessor? processor = null;
            try
            {
                processor = new SteamAudioProcessor();
                processor.Initialize(new AudioEffectsSettings { SampleRate = 44100, FrameSize = 1024 });
            }
            catch (Exception ex) when (ex.Message.Contains("phonon", StringComparison.OrdinalIgnoreCase)
                                    || ex is DllNotFoundException
                                    || ex is InvalidOperationException)
            {
                processor?.Dispose();
                Assert.Inconclusive($"Steam Audio unavailable — skipping: {ex.Message}");
                return;
            }

            using (processor)
            {
                processor.SetListenerPose(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);

                var h = processor.AddSource(new AudioEffectsSourceSettings());
                processor.SetSourcePose(h, new Vector3(3, 0, 0), -Vector3.UnitZ);
                processor.Tick(0.016f);

                processor.RemoveSource(h);
            }
        }

        [Test]
        public void SteamAudioProcessor_ProcessBuffer_ProducesOutput()
        {
            SteamAudioProcessor? processor = null;
            try
            {
                processor = new SteamAudioProcessor();
                processor.Initialize(new AudioEffectsSettings { SampleRate = 44100, FrameSize = 256 });
            }
            catch (Exception ex) when (ex.Message.Contains("phonon", StringComparison.OrdinalIgnoreCase)
                                    || ex is DllNotFoundException
                                    || ex is InvalidOperationException)
            {
                processor?.Dispose();
                Assert.Inconclusive($"Steam Audio unavailable — skipping: {ex.Message}");
                return;
            }

            using (processor)
            {
                processor.SetListenerPose(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);

                var h = processor.AddSource(new AudioEffectsSourceSettings
                {
                    Position = new Vector3(5, 0, 0),
                    Forward = -Vector3.UnitZ,
                });

                processor.Tick(0.016f);

                // Generate a simple mono tone as input (256 samples)
                var input = new float[256];
                for (int i = 0; i < input.Length; i++)
                    input[i] = MathF.Sin(2 * MathF.PI * 440 * i / 44100f) * 0.5f;

                // Output is stereo (256 frames × 2 channels = 512 samples)
                var output = new float[512];

                processor.ProcessBuffer(h, input, output, 1, 44100);

                // Output should not be all zeros (HRTF should produce some signal)
                bool anyNonZero = false;
                for (int i = 0; i < output.Length; i++)
                {
                    if (MathF.Abs(output[i]) > 1e-8f)
                    {
                        anyNonZero = true;
                        break;
                    }
                }
                anyNonZero.ShouldBeTrue("ProcessBuffer should produce non-zero HRTF-spatialized output.");

                processor.RemoveSource(h);
            }
        }

        [Test]
        public void SteamAudioProcessor_InitializeShutdown_Idempotent()
        {
            SteamAudioProcessor? processor = null;
            try
            {
                processor = new SteamAudioProcessor();
                processor.Initialize(new AudioEffectsSettings { SampleRate = 44100, FrameSize = 1024 });
            }
            catch (Exception ex) when (ex.Message.Contains("phonon", StringComparison.OrdinalIgnoreCase)
                                    || ex is DllNotFoundException
                                    || ex is InvalidOperationException)
            {
                processor?.Dispose();
                Assert.Inconclusive($"Steam Audio unavailable — skipping: {ex.Message}");
                return;
            }

            using (processor)
            {
                // Double init should be harmless
                processor.Initialize(new AudioEffectsSettings { SampleRate = 48000, FrameSize = 512 });

                processor.Shutdown();
                // Double shutdown should be harmless
                processor.Shutdown();
            }
        }

        #endregion
    }
}
