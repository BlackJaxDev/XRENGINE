using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Audio;

namespace XREngine.UnitTests.Audio
{
    /// <summary>
    /// OpenAL regression baseline tests for Phase 0 of the audio architecture migration.
    /// These tests codify the current OpenAL behavior so that any future transport/effects
    /// split can be validated against this baseline.
    ///
    /// Tests that require a real OpenAL device will mark themselves as Inconclusive when
    /// no audio hardware is available (e.g. headless CI).
    /// </summary>
    [TestFixture]
    public sealed class OpenALRegressionTests
    {
        private ListenerContext? _listener;

        [SetUp]
        public void SetUp()
        {
            try
            {
                _listener = new ListenerContext();
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"OpenAL device unavailable — skipping: {ex.Message}");
            }
        }

        [TearDown]
        public void TearDown()
        {
            _listener?.Dispose();
            _listener = null;
        }

        private ListenerContext Listener
        {
            get
            {
                if (_listener is null)
                    Assert.Inconclusive("No listener available.");
                return _listener!;
            }
        }

        #region Feature Flag

        [Test]
        public void AudioArchitectureV2_DefaultsFalse()
        {
            AudioSettings.AudioArchitectureV2.ShouldBeFalse(
                "AudioArchitectureV2 must default to false so the legacy OpenAL path is always used until opted-in.");
        }

        #endregion

        #region Listener Pose

        [Test]
        public void Listener_Position_RoundTrips()
        {
            var pos = new Vector3(1.5f, 2.5f, -3.0f);
            Listener.Position = pos;
            var read = Listener.Position;
            read.X.ShouldBe(pos.X, 1e-4f);
            read.Y.ShouldBe(pos.Y, 1e-4f);
            read.Z.ShouldBe(pos.Z, 1e-4f);
        }

        [Test]
        public void Listener_Velocity_RoundTrips()
        {
            var vel = new Vector3(0.1f, -0.2f, 0.3f);
            Listener.Velocity = vel;
            var read = Listener.Velocity;
            read.X.ShouldBe(vel.X, 1e-4f);
            read.Y.ShouldBe(vel.Y, 1e-4f);
            read.Z.ShouldBe(vel.Z, 1e-4f);
        }

        [Test]
        public void Listener_Orientation_RoundTrips()
        {
            var forward = Vector3.Normalize(new Vector3(1, 0, -1));
            var up = new Vector3(0, 1, 0);
            Listener.SetOrientation(forward, up);
            Listener.GetOrientation(out var readFwd, out var readUp);
            readFwd.X.ShouldBe(forward.X, 1e-4f);
            readFwd.Y.ShouldBe(forward.Y, 1e-4f);
            readFwd.Z.ShouldBe(forward.Z, 1e-4f);
            readUp.X.ShouldBe(up.X, 1e-4f);
            readUp.Y.ShouldBe(up.Y, 1e-4f);
            readUp.Z.ShouldBe(up.Z, 1e-4f);
        }

        #endregion

        #region Listener Gain & Enable

        [Test]
        public void Listener_Gain_DefaultsTo1()
        {
            Listener.Gain.ShouldBe(1.0f);
        }

        [Test]
        public void Listener_GainScale_DefaultsTo1()
        {
            Listener.GainScale.ShouldBe(1.0f);
        }

        [Test]
        public void Listener_Enabled_DefaultsTrue()
        {
            Listener.Enabled.ShouldBeTrue();
        }

        [Test]
        public void Listener_SetGain_Persists()
        {
            Listener.Gain = 0.5f;
            Listener.Gain.ShouldBe(0.5f);
        }

        #endregion

        #region Source Lifecycle & State Transitions

        [Test]
        public void Source_TakeAndRelease_Succeeds()
        {
            var source = Listener.TakeSource();
            source.ShouldNotBeNull();
            source.Handle.ShouldNotBe(0u);
            Listener.Sources.Count.ShouldBe(1);

            Listener.ReleaseSource(source);
            Listener.Sources.Count.ShouldBe(0);
        }

        [Test]
        public void Source_InitialState_IsInitial()
        {
            var source = Listener.TakeSource();
            source.SourceState.ShouldBe(AudioSource.ESourceState.Initial);
            Listener.ReleaseSource(source);
        }

        [Test]
        public void Source_PlayStopPauseRewind_StateTransitions()
        {
            var source = Listener.TakeSource();
            var buffer = Listener.TakeBuffer();

            // Need a buffer attached for Play to transition from Initial
            short[] silence = new short[4410]; // ~100ms @ 44100 mono16
            buffer.SetData(silence, 44100, false);
            source.Buffer = buffer;

            // Initial → Playing
            source.Play();
            source.SourceState.ShouldBe(AudioSource.ESourceState.Playing);

            // Playing → Paused
            source.Pause();
            source.SourceState.ShouldBe(AudioSource.ESourceState.Paused);

            // Paused → Playing
            source.Play();
            source.SourceState.ShouldBe(AudioSource.ESourceState.Playing);

            // Playing → Stopped
            source.Stop();
            source.SourceState.ShouldBe(AudioSource.ESourceState.Stopped);

            // Stopped → Initial (Rewind)
            source.Rewind();
            source.SourceState.ShouldBe(AudioSource.ESourceState.Initial);

            source.Buffer = null;
            Listener.ReleaseBuffer(buffer);
            Listener.ReleaseSource(source);
        }

        #endregion

        #region Source Properties

        [Test]
        public void Source_Gain_RoundTrips()
        {
            var source = Listener.TakeSource();
            source.Gain = 0.75f;
            source.Gain.ShouldBe(0.75f, 1e-4f);
            Listener.ReleaseSource(source);
        }

        [Test]
        public void Source_Pitch_RoundTrips()
        {
            var source = Listener.TakeSource();
            source.Pitch = 1.5f;
            source.Pitch.ShouldBe(1.5f, 1e-4f);
            Listener.ReleaseSource(source);
        }

        [Test]
        public void Source_Position_RoundTrips()
        {
            var source = Listener.TakeSource();
            var pos = new Vector3(10, 20, 30);
            source.Position = pos;
            var read = source.Position;
            read.X.ShouldBe(pos.X, 1e-4f);
            read.Y.ShouldBe(pos.Y, 1e-4f);
            read.Z.ShouldBe(pos.Z, 1e-4f);
            Listener.ReleaseSource(source);
        }

        [Test]
        public void Source_Looping_RoundTrips()
        {
            var source = Listener.TakeSource();
            source.Looping = true;
            source.Looping.ShouldBeTrue();
            source.Looping = false;
            source.Looping.ShouldBeFalse();
            Listener.ReleaseSource(source);
        }

        [Test]
        public void Source_RelativeToListener_RoundTrips()
        {
            var source = Listener.TakeSource();
            source.RelativeToListener = true;
            source.RelativeToListener.ShouldBeTrue();
            source.RelativeToListener = false;
            source.RelativeToListener.ShouldBeFalse();
            Listener.ReleaseSource(source);
        }

        [Test]
        public void Source_DistanceModel_Properties_RoundTrip()
        {
            var source = Listener.TakeSource();

            source.ReferenceDistance = 2.0f;
            source.ReferenceDistance.ShouldBe(2.0f, 1e-4f);

            source.MaxDistance = 100.0f;
            source.MaxDistance.ShouldBe(100.0f, 1e-4f);

            source.RolloffFactor = 0.5f;
            source.RolloffFactor.ShouldBe(0.5f, 1e-4f);

            Listener.ReleaseSource(source);
        }

        [Test]
        public void Source_ConeAngles_RoundTrip()
        {
            var source = Listener.TakeSource();

            source.ConeInnerAngle = 90.0f;
            source.ConeInnerAngle.ShouldBe(90.0f, 1e-4f);

            source.ConeOuterAngle = 180.0f;
            source.ConeOuterAngle.ShouldBe(180.0f, 1e-4f);

            source.ConeOuterGain = 0.5f;
            source.ConeOuterGain.ShouldBe(0.5f, 1e-4f);

            Listener.ReleaseSource(source);
        }

        #endregion

        #region Buffer Operations

        [Test]
        public void Buffer_TakeAndRelease_Succeeds()
        {
            var buffer = Listener.TakeBuffer();
            buffer.ShouldNotBeNull();
            buffer.Handle.ShouldNotBe(0u);
            Listener.Buffers.Count.ShouldBe(1);

            Listener.ReleaseBuffer(buffer);
            Listener.Buffers.Count.ShouldBe(0);
        }

        [Test]
        public void Buffer_SetData_Mono16_Succeeds()
        {
            var buffer = Listener.TakeBuffer();
            short[] data = new short[1024];
            buffer.SetData(data, 44100, false);
            buffer.Frequency.ShouldBe(44100);
            buffer.Stereo.ShouldBeFalse();
            Listener.ReleaseBuffer(buffer);
        }

        [Test]
        public void Buffer_SetData_Stereo16_Succeeds()
        {
            var buffer = Listener.TakeBuffer();
            short[] data = new short[2048];
            buffer.SetData(data, 48000, true);
            buffer.Frequency.ShouldBe(48000);
            buffer.Stereo.ShouldBeTrue();
            Listener.ReleaseBuffer(buffer);
        }

        [Test]
        public void Buffer_SetData_Mono8_Succeeds()
        {
            var buffer = Listener.TakeBuffer();
            byte[] data = new byte[512];
            buffer.SetData(data, 22050, false);
            buffer.Frequency.ShouldBe(22050);
            buffer.Stereo.ShouldBeFalse();
            Listener.ReleaseBuffer(buffer);
        }

        #endregion

        #region Streaming Queue / Unqueue

        [Test]
        public void Source_QueueBuffers_IncreasesQueueCount()
        {
            var source = Listener.TakeSource();
            var buf1 = Listener.TakeBuffer();
            var buf2 = Listener.TakeBuffer();

            short[] silence = new short[4410];
            buf1.SetData(silence, 44100, false);
            buf2.SetData(silence, 44100, false);

            // Disable auto-play so we can inspect queue without playback
            source.AutoPlayOnQueue = false;

            source.QueueBuffers(10, buf1, buf2);
            source.BuffersQueued.ShouldBe(2);
            source.CurrentStreamingBuffers.Count.ShouldBe(2);

            // Cleanup — stop and release
            source.Stop();
            Listener.ReleaseSource(source);
        }

        [Test]
        public void Source_QueueBuffers_OverflowReturnsBuffersToPool()
        {
            var source = Listener.TakeSource();
            var buf1 = Listener.TakeBuffer();

            short[] silence = new short[4410];
            buf1.SetData(silence, 44100, false);

            source.AutoPlayOnQueue = false;
            source.QueueBuffers(1, buf1);
            source.BuffersQueued.ShouldBe(1);

            // Now queue another — should overflow since maxbuffers=1
            var buf2 = Listener.TakeBuffer();
            buf2.SetData(silence, 44100, false);
            bool result = source.QueueBuffers(1, buf2);
            result.ShouldBeFalse("Queue should be full");

            source.Stop();
            Listener.ReleaseSource(source);
        }

        #endregion

        #region Distance Model

        [Test]
        public void Listener_DistanceModel_RoundTrips()
        {
            Listener.DistanceModel = EDistanceModel.LinearDistanceClamped;
            Listener.DistanceModel.ShouldBe(EDistanceModel.LinearDistanceClamped);

            Listener.DistanceModel = EDistanceModel.InverseDistanceClamped;
            Listener.DistanceModel.ShouldBe(EDistanceModel.InverseDistanceClamped);
        }

        [Test]
        public void Listener_CalcGain_InverseDistance_ReturnsExpected()
        {
            Listener.DistanceModel = EDistanceModel.InverseDistance;
            Listener.Position = Vector3.Zero;

            // At reference distance, gain should be 1.0
            float gain = Listener.CalcGain(new Vector3(1, 0, 0), referenceDistance: 1.0f, maxDistance: 100f, rolloffFactor: 1.0f);
            gain.ShouldBe(1.0f, 1e-4f);

            // At double reference distance with rolloff=1, gain = refDist / (refDist + rolloff*(dist-refDist)) = 1/(1+1) = 0.5
            gain = Listener.CalcGain(new Vector3(2, 0, 0), referenceDistance: 1.0f, maxDistance: 100f, rolloffFactor: 1.0f);
            gain.ShouldBe(0.5f, 1e-4f);
        }

        [Test]
        public void Listener_DopplerFactor_RoundTrips()
        {
            Listener.DopplerFactor = 2.0f;
            Listener.DopplerFactor.ShouldBe(2.0f, 1e-4f);
        }

        [Test]
        public void Listener_SpeedOfSound_RoundTrips()
        {
            Listener.SpeedOfSound = 343.3f;
            Listener.SpeedOfSound.ShouldBe(343.3f, 0.1f);
        }

        #endregion

        #region Fade

        [Test]
        public void Listener_FadeIn_ProgressesGainScale()
        {
            Listener.GainScale = 0.0f;
            Listener.FadeInSeconds = 1.0f; // fade in over 1 second

            // Simulate a tick of 0.5 seconds
            Listener.Tick(0.5f);
            Listener.GainScale.ShouldBeGreaterThan(0.0f);
            Listener.GainScale.ShouldBeLessThan(1.0f);

            // Simulate another tick to complete the fade
            Listener.Tick(0.6f);
            Listener.GainScale.ShouldBe(1.0f);
            Listener.FadeInSeconds.ShouldBeNull("Fade should complete and clear FadeInSeconds");
        }

        [Test]
        public void Listener_FadeOut_ProgressesGainScale()
        {
            Listener.GainScale = 1.0f;
            Listener.FadeInSeconds = -1.0f; // negative = fade out over 1 second

            // Simulate a tick of 0.5 seconds
            Listener.Tick(0.5f);
            Listener.GainScale.ShouldBeLessThan(1.0f);
            Listener.GainScale.ShouldBeGreaterThan(0.0f);

            // Complete the fade
            Listener.Tick(0.6f);
            Listener.GainScale.ShouldBe(0.0f);
            Listener.FadeInSeconds.ShouldBeNull("Fade should complete and clear FadeInSeconds");
        }

        #endregion

        #region EFX Path

        [Test]
        public void Listener_EFX_EffectContext_Exists_IfSupported()
        {
            // EffectContext is non-null only if the OpenAL implementation supports EFX.
            // We don't fail if it's null — just note the capability.
            if (Listener.Effects is null)
            {
                Assert.Inconclusive("OpenAL EFX extension not available on this device.");
                return;
            }

            Listener.Effects.ShouldNotBeNull();
            Listener.Effects.Listener.ShouldBe(Listener);
        }

        [Test]
        public void Source_EFX_DirectFilter_DefaultsToZero()
        {
            if (Listener.Effects is null)
            {
                Assert.Inconclusive("OpenAL EFX extension not available.");
                return;
            }

            var source = Listener.TakeSource();
            source.DirectFilter.ShouldBe(0);
            Listener.ReleaseSource(source);
        }

        #endregion

        #region Multiple Listeners

        [Test]
        public void AudioManager_NewListener_TracksMultipleListeners()
        {
            // Use AudioManager to create listeners the normal way
            var mgr = new AudioManager();
            ListenerContext? l1 = null;
            ListenerContext? l2 = null;
            try
            {
                l1 = mgr.NewListener("test1");
                l2 = mgr.NewListener("test2");
                mgr.Listeners.Count.ShouldBe(2);
            }
            finally
            {
                l1?.Dispose();
                l2?.Dispose();
            }
            // After dispose, listeners should be removed
            mgr.Listeners.Count.ShouldBe(0);
        }

        #endregion

        #region Context Extensions

        [Test]
        public void Listener_GetVendor_ReturnsNonEmpty()
        {
            Listener.GetVendor().ShouldNotBeNullOrEmpty();
        }

        [Test]
        public void Listener_GetRenderer_ReturnsNonEmpty()
        {
            Listener.GetRenderer().ShouldNotBeNullOrEmpty();
        }

        [Test]
        public void Listener_GetVersion_ReturnsNonEmpty()
        {
            Listener.GetVersion().ShouldNotBeNullOrEmpty();
        }

        [Test]
        public void Listener_GetExtensions_ReturnsAtLeastOne()
        {
            var extensions = Listener.GetExtensions();
            extensions.ShouldNotBeNull();
            extensions.Length.ShouldBeGreaterThan(0);
        }

        #endregion
    }
}
