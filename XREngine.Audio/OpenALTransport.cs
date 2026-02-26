using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;
using Silk.NET.OpenAL.Extensions.EXT;
using System.Diagnostics;
using System.Numerics;

namespace XREngine.Audio
{
    /// <summary>
    /// OpenAL implementation of <see cref="IAudioTransport"/>.
    /// Owns the OpenAL device, context, source/buffer lifecycle, listener state,
    /// and all format/capture extensions.
    /// <para>
    /// Extracted from the original monolithic <see cref="ListenerContext"/> to support
    /// the transport/effects split architecture.
    /// </para>
    /// </summary>
    public sealed unsafe class OpenALTransport : IAudioTransport
    {
        // --- OpenAL core objects ---

        public AL Api { get; }
        public ALContext Context { get; }
        internal Device* DeviceHandle { get; }
        internal Context* ContextHandle { get; }

        // --- Format extensions ---

        public VorbisFormat? VorbisFormat { get; }
        public MP3Format? MP3Format { get; }
        public XRam? XRam { get; }
        public MultiChannelBuffers? MultiChannel { get; }
        public DoubleFormat? DoubleFormat { get; }
        public MULAWFormat? MuLawFormat { get; }
        public FloatFormat? FloatFormat { get; }
        public MCFormats? MCFormats { get; }
        public ALAWFormat? ALawFormat { get; }

        // --- EFX extension (exposed for OpenALEfxProcessor) ---

        internal EffectExtension? EffectExtension { get; }

        // --- Capture ---

        public Capture? Capture { get; }

        // --- State ---

        public string? DeviceName { get; private set; }
        public int SampleRate { get; private set; } = 44100;
        public bool IsOpen { get; private set; }

        /// <summary>
        /// Tracks which transport is "current" for the OpenAL context.
        /// OpenAL uses a thread-global current context; we must ensure ours is active.
        /// </summary>
        public static OpenALTransport? CurrentTransport { get; private set; }

        public OpenALTransport(string? deviceName = null)
        {
            Api = AL.GetApi();

            // Probe format extensions
            VorbisFormat = Api.TryGetExtension<VorbisFormat>(out var vf) ? vf : null;
            MP3Format = Api.TryGetExtension<MP3Format>(out var mp3) ? mp3 : null;
            MultiChannel = Api.TryGetExtension<MultiChannelBuffers>(out var mc) ? mc : null;
            DoubleFormat = Api.TryGetExtension<DoubleFormat>(out var df) ? df : null;
            MuLawFormat = Api.TryGetExtension<MULAWFormat>(out var mul) ? mul : null;
            FloatFormat = Api.TryGetExtension<FloatFormat>(out var ff) ? ff : null;
            MCFormats = Api.TryGetExtension<MCFormats>(out var mcf) ? mcf : null;
            ALawFormat = Api.TryGetExtension<ALAWFormat>(out var alf) ? alf : null;
            XRam = Api.TryGetExtension<XRam>(out var xram) ? xram : null;
            EffectExtension = Api.TryGetExtension<EffectExtension>(out var efx) ? efx : null;

            // Open device and context
            Context = ALContext.GetApi(false);
            DeviceHandle = Context.OpenDevice(deviceName);
            ContextHandle = Context.CreateContext(DeviceHandle, null);
            DeviceName = deviceName;
            IsOpen = true;

            MakeCurrent();
            VerifyError();
        }

        // --- Context management ---

        public void MakeCurrent()
        {
            if (CurrentTransport == this)
                return;

            CurrentTransport = this;
            Context.MakeContextCurrent(ContextHandle);
        }

        public void VerifyError()
        {
            if (CurrentTransport != this)
                return;

            var error = Api.GetError();
            if (error != AudioError.NoError)
            {
                Trace.WriteLine($"OpenAL Error: {error}");
                AudioDiagnostics.RecordOpenALError($"{error}");
            }
        }

        // --- IAudioTransport: Device ---

        public void Open(string? deviceName = null)
        {
            // Already opened in constructor for OpenAL (device must exist at construction time).
            // This method exists for transports that support deferred open.
        }

        public void Close()
        {
            if (!IsOpen)
                return;

            IsOpen = false;
            Context.DestroyContext(ContextHandle);
            Context.CloseDevice(DeviceHandle);

            if (CurrentTransport == this)
                CurrentTransport = null;
        }

        // --- IAudioTransport: Listener ---

        public void SetListenerPosition(Vector3 position)
        {
            MakeCurrent();
            Api.SetListenerProperty(ListenerVector3.Position, position);
            VerifyError();
        }

        public void SetListenerVelocity(Vector3 velocity)
        {
            MakeCurrent();
            Api.SetListenerProperty(ListenerVector3.Velocity, velocity);
            VerifyError();
        }

        public void SetListenerOrientation(Vector3 forward, Vector3 up)
        {
            MakeCurrent();
            float[] orientation = [forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z];
            fixed (float* pOrientation = orientation)
                Api.SetListenerProperty(ListenerFloatArray.Orientation, pOrientation);
            VerifyError();
        }

        public void SetListenerGain(float gain)
        {
            MakeCurrent();
            Api.SetListenerProperty(ListenerFloat.Gain, gain);
            VerifyError();
        }

        // --- Listener getters (not in interface, used internally) ---

        internal Vector3 GetListenerPosition()
        {
            MakeCurrent();
            Api.GetListenerProperty(ListenerVector3.Position, out Vector3 position);
            VerifyError();
            return position;
        }

        internal Vector3 GetListenerVelocity()
        {
            MakeCurrent();
            Api.GetListenerProperty(ListenerVector3.Velocity, out Vector3 velocity);
            VerifyError();
            return velocity;
        }

        internal void GetListenerOrientation(out Vector3 forward, out Vector3 up)
        {
            MakeCurrent();
            float[] orientation = new float[6];
            fixed (float* pOrientation = orientation)
                Api.GetListenerProperty(ListenerFloatArray.Orientation, pOrientation);
            VerifyError();
            forward = new Vector3(orientation[0], orientation[1], orientation[2]);
            up = new Vector3(orientation[3], orientation[4], orientation[5]);
        }

        internal float GetListenerGain()
        {
            MakeCurrent();
            Api.GetListenerProperty(ListenerFloat.Gain, out float gain);
            VerifyError();
            return gain;
        }

        // --- IAudioTransport: Source lifecycle ---

        public AudioSourceHandle CreateSource()
        {
            MakeCurrent();
            uint id = Api.GenSource();
            VerifyError();
            return new AudioSourceHandle(id);
        }

        public void DestroySource(AudioSourceHandle source)
        {
            if (!source.IsValid)
                return;
            MakeCurrent();
            Api.SourceStop(source.Id);
            Api.DeleteSource(source.Id);
            VerifyError();
        }

        // --- IAudioTransport: Buffer lifecycle ---

        public AudioBufferHandle CreateBuffer()
        {
            MakeCurrent();
            uint id = Api.GenBuffer();
            VerifyError();
            return new AudioBufferHandle(id);
        }

        public void DestroyBuffer(AudioBufferHandle buffer)
        {
            if (!buffer.IsValid)
                return;
            MakeCurrent();
            Api.DeleteBuffer(buffer.Id);
            VerifyError();
        }

        public void UploadBufferData(AudioBufferHandle buffer, ReadOnlySpan<byte> pcm, int frequency, int channels, SampleFormat format)
        {
            MakeCurrent();

            // Use pointer-based BufferData to avoid generic overload resolution issues
            // with ReadOnlySpan<byte>. All PCM data is passed as raw bytes regardless
            // of sample format — the OpenAL format enum handles interpretation.
            bool stereo = channels >= 2;
            fixed (byte* ptr = pcm)
            {
                switch (format)
                {
                    case SampleFormat.Byte:
                        Api.BufferData(buffer.Id, stereo ? BufferFormat.Stereo8 : BufferFormat.Mono8, ptr, pcm.Length, frequency);
                        break;
                    case SampleFormat.Short:
                        Api.BufferData(buffer.Id, stereo ? BufferFormat.Stereo16 : BufferFormat.Mono16, ptr, pcm.Length, frequency);
                        break;
                    case SampleFormat.Float:
                        if (FloatFormat is not null)
                        {
                            Api.BufferData(buffer.Id, stereo ? FloatBufferFormat.Stereo : FloatBufferFormat.Mono, ptr, pcm.Length, frequency);
                        }
                        else
                        {
                            Trace.WriteLine("OpenAL float format extension not available; cannot upload float PCM.");
                        }
                        break;
                }
            }

            VerifyError();
        }

        // --- IAudioTransport: Playback ---

        public void Play(AudioSourceHandle source)
        {
            MakeCurrent();
            Api.SourcePlay(source.Id);
            VerifyError();
        }

        public void Stop(AudioSourceHandle source)
        {
            MakeCurrent();
            Api.SourceStop(source.Id);
            VerifyError();
        }

        public void Pause(AudioSourceHandle source)
        {
            MakeCurrent();
            Api.SourcePause(source.Id);
            VerifyError();
        }

        public void SetSourceBuffer(AudioSourceHandle source, AudioBufferHandle buffer)
        {
            MakeCurrent();
            Api.SetSourceProperty(source.Id, SourceInteger.Buffer, buffer.Id);
            VerifyError();
        }

        public void QueueBuffers(AudioSourceHandle source, ReadOnlySpan<AudioBufferHandle> buffers)
        {
            MakeCurrent();
            uint* handles = stackalloc uint[buffers.Length];
            for (int i = 0; i < buffers.Length; i++)
                handles[i] = buffers[i].Id;
            Api.SourceQueueBuffers(source.Id, buffers.Length, handles);
            VerifyError();
        }

        public int UnqueueProcessedBuffers(AudioSourceHandle source, Span<AudioBufferHandle> output)
        {
            MakeCurrent();
            int processed = GetSourcePropertyInt(source.Id, GetSourceInteger.BuffersProcessed);
            int count = Math.Min(processed, output.Length);
            if (count <= 0)
                return 0;

            uint* handles = stackalloc uint[count];
            Api.SourceUnqueueBuffers(source.Id, count, handles);
            VerifyError();

            for (int i = 0; i < count; i++)
                output[i] = new AudioBufferHandle(handles[i]);

            return count;
        }

        // --- IAudioTransport: Source properties ---

        public void SetSourcePosition(AudioSourceHandle source, Vector3 position)
        {
            MakeCurrent();
            Api.SetSourceProperty(source.Id, SourceVector3.Position, position);
            VerifyError();
        }

        public void SetSourceVelocity(AudioSourceHandle source, Vector3 velocity)
        {
            MakeCurrent();
            Api.SetSourceProperty(source.Id, SourceVector3.Velocity, velocity);
            VerifyError();
        }

        public void SetSourceGain(AudioSourceHandle source, float gain)
        {
            MakeCurrent();
            Api.SetSourceProperty(source.Id, SourceFloat.Gain, gain);
            VerifyError();
        }

        public void SetSourcePitch(AudioSourceHandle source, float pitch)
        {
            MakeCurrent();
            Api.SetSourceProperty(source.Id, SourceFloat.Pitch, pitch);
            VerifyError();
        }

        public void SetSourceLooping(AudioSourceHandle source, bool loop)
        {
            MakeCurrent();
            Api.SetSourceProperty(source.Id, SourceBoolean.Looping, loop);
            VerifyError();
        }

        public bool IsSourcePlaying(AudioSourceHandle source)
        {
            MakeCurrent();
            int state = GetSourcePropertyInt(source.Id, GetSourceInteger.SourceState);
            VerifyError();
            return (SourceState)state == SourceState.Playing;
        }

        // --- IAudioTransport: Capture ---

        public IAudioCaptureDevice? OpenCaptureDevice(string? device, int sampleRate, SampleFormat format, int bufferSize)
        {
            // Capture stays OpenAL-specific. AudioInputDevice/AudioInputDeviceFloat
            // continue to use ListenerContext.Capture and DeviceHandle directly.
            // Full capture abstraction deferred to a later phase.
            return null;
        }

        // --- Extended OpenAL state (not in IAudioTransport, used by ListenerContext) ---

        internal float GetDopplerFactor()
        {
            MakeCurrent();
            var factor = Api.GetStateProperty(StateFloat.DopplerFactor);
            VerifyError();
            return factor;
        }

        internal void SetDopplerFactor(float factor)
        {
            MakeCurrent();
            Api.DopplerFactor(factor);
            VerifyError();
        }

        internal float GetSpeedOfSound()
        {
            MakeCurrent();
            var speed = Api.GetStateProperty(StateFloat.SpeedOfSound);
            VerifyError();
            return speed;
        }

        internal void SetSpeedOfSound(float speed)
        {
            MakeCurrent();
            Api.SpeedOfSound(speed);
            VerifyError();
        }

        internal DistanceModel GetDistanceModel()
        {
            MakeCurrent();
            var model = (DistanceModel)Api.GetStateProperty(StateInteger.DistanceModel);
            VerifyError();
            return model;
        }

        internal void SetDistanceModel(DistanceModel model)
        {
            MakeCurrent();
            Api.DistanceModel(model);
            VerifyError();
        }

        internal bool IsExtensionPresent(string extension)
            => Api.IsExtensionPresent(extension);

        internal string GetVendor()
            => Api.GetStateProperty(StateString.Vendor);
        internal string GetRenderer()
            => Api.GetStateProperty(StateString.Renderer);
        internal string GetVersion()
            => Api.GetStateProperty(StateString.Version);
        internal string[] GetExtensions()
            => Api.GetStateProperty(StateString.Extensions).Split(' ');

        // --- Helpers ---

        private int GetSourcePropertyInt(uint sourceId, GetSourceInteger param)
        {
            Api.GetSourceProperty(sourceId, param, out int value);
            VerifyError();
            return value;
        }

        // --- IDisposable ---

        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }
    }
}
