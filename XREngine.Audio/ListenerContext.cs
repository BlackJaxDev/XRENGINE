using Extensions;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;
using Silk.NET.OpenAL.Extensions.Enumeration;
using Silk.NET.OpenAL.Extensions.EXT;
using System.Diagnostics;
using System.Numerics;
using XREngine.Core;
using XREngine.Data.Core;

namespace XREngine.Audio
{
    public sealed unsafe class ListenerContext : XRBase, IDisposable
    {
        //TODO: implement audio source priority
        //destroy sources with lower priority first to make room for higher priority sources.
        //0 is the lowest priority, 255 is the highest priority.

        public string? Name { get; set; }

        public AL Api { get; } = AL.GetApi();
        public ALContext Context { get; }

        internal Device* DeviceHandle { get; }
        internal Context* ContextHandle { get; }

        public EffectContext? Effects { get; } = null;
        public VorbisFormat? VorbisFormat { get; } = null;
        public MP3Format? MP3Format { get; } = null;
        public XRam? XRam { get; } = null;
        public MultiChannelBuffers? MultiChannel { get; } = null;
        public DoubleFormat? DoubleFormat { get; } = null;
        public MULAWFormat? MuLawFormat { get; } = null;
        public FloatFormat? FloatFormat { get; } = null;
        public MCFormats? MCFormats { get; } = null;
        public ALAWFormat? ALawFormat { get; } = null;

        public Capture? Capture { get; } = null;

        public EventDictionary<uint, AudioSource> Sources { get; } = [];
        public EventDictionary<uint, AudioBuffer> Buffers { get; } = [];

        internal ListenerContext()
        {
            if (Api.TryGetExtension<VorbisFormat>(out var vorbisFormat))
                VorbisFormat = vorbisFormat;
            if (Api.TryGetExtension<MP3Format>(out var mp3Format))
                MP3Format = mp3Format;
            if (Api.TryGetExtension<MultiChannelBuffers>(out var multiChannel))
                MultiChannel = multiChannel;
            if (Api.TryGetExtension<DoubleFormat>(out var doubleFormat))
                DoubleFormat = doubleFormat;
            if (Api.TryGetExtension<MULAWFormat>(out var mulawFormat))
                MuLawFormat = mulawFormat;
            if (Api.TryGetExtension<FloatFormat>(out var floatFormat))
                FloatFormat = floatFormat;
            if (Api.TryGetExtension<MCFormats>(out var mcFormats))
                MCFormats = mcFormats;
            if (Api.TryGetExtension<ALAWFormat>(out var alawFormat))
                ALawFormat = alawFormat;

            if (Api.TryGetExtension<EffectExtension>(out var effectExtension))
                Effects = new EffectContext(this, effectExtension);
            if (Api.TryGetExtension<XRam>(out var xram))
                XRam = xram;

            Context = ALContext.GetApi(false);

            //string deviceSpecifier = "";
            //if (Context.TryGetExtension<Enumeration>(null, out var e))
            //{
            //    var stringList = e.GetStringList(GetEnumerationContextStringList.DeviceSpecifiers);
            //    foreach (var device in stringList)
            //    {
            //        Debug.WriteLine($"Found audio device \"{device}\"");
            //        deviceSpecifier = device;
            //    }
            //    e.Dispose();
            //}

            DeviceHandle = Context.OpenDevice(null);
            ContextHandle = Context.CreateContext(DeviceHandle, null);
            //if (Context.TryGetExtension<Capture>(DeviceHandle, out var captureExtension))
            //    Capture = captureExtension;
            MakeCurrent();
            VerifyError();

            _gain = GetGain();

            SourcePool = new ResourcePool<AudioSource>(() => new AudioSource(this));
            BufferPool = new ResourcePool<AudioBuffer>(() => new AudioBuffer(this));
        }

        public static ListenerContext? CurrentContext { get; private set; }

        public void MakeCurrent()
        {
            if (CurrentContext == this)
                return;

            CurrentContext = this;
            Context.MakeContextCurrent(ContextHandle);
        }

        public void VerifyError()
        {
            if (CurrentContext != this)
                return;

            var error = Api.GetError();
            if (error != AudioError.NoError)
                Trace.WriteLine($"OpenAL Error: {error}");
        }

        private ResourcePool<AudioSource> SourcePool { get; }
        private ResourcePool<AudioBuffer> BufferPool { get; }

        public AudioSource TakeSource()
        {
            var source = SourcePool.Take();
            Sources.Add(source.Handle, source);
            VerifyError();
            return source;
        }
        public AudioBuffer TakeBuffer()
        {
            var buffer = BufferPool.Take();
            Buffers.Add(buffer.Handle, buffer);
            VerifyError();
            return buffer;
        }

        public void ReleaseSource(AudioSource source)
        {
            if (source is null)
                return;
            Sources.Remove(source.Handle);
            SourcePool.Release(source);
            VerifyError();
        }
        public void ReleaseBuffer(AudioBuffer buffer)
        {
            if (buffer is null)
                return;
            Buffers.Remove(buffer.Handle);
            BufferPool.Release(buffer);
            VerifyError();
        }

        public void DestroyUnusedSources(int count)
            => SourcePool.Destroy(count);
        public void DestroyUnusedBuffers(int count)
            => BufferPool.Destroy(count);

        public AudioSource? GetSourceByHandle(uint handle)
            => Sources.TryGetValue(handle, out AudioSource? source) ? source : null;
        public AudioBuffer? GetBufferByHandle(uint handle)
            => Buffers.TryGetValue(handle, out AudioBuffer? buffer) ? buffer : null;

        public bool IsExtensionPresent(string extension)
            => Api.IsExtensionPresent(extension);

        public bool HasDopplerFactorSet()
            => Api.GetStateProperty(StateBoolean.HasDopplerFactor);
        public bool HasDopplerVelocitySet()
            => Api.GetStateProperty(StateBoolean.HasDopplerVelocity);
        public bool HasSpeedOfSoundSet()
            => Api.GetStateProperty(StateBoolean.HasSpeedOfSound);
        public bool IsDistanceModelInverseDistanceClamped()
            => Api.GetStateProperty(StateBoolean.IsDistanceModelInverseDistanceClamped);

        public string GetVendor()
            => Api.GetStateProperty(StateString.Vendor);
        public string GetRenderer()
            => Api.GetStateProperty(StateString.Renderer);
        public string GetVersion()
            => Api.GetStateProperty(StateString.Version);
        public string[] GetExtensions()
            => Api.GetStateProperty(StateString.Extensions).Split(' ');

        public float DopplerFactor
        {
            get => GetDopplerFactor();
            set => SetDopplerFactor(value);
        }
        public float SpeedOfSound
        {
            get => GetSpeedOfSound();
            set => SetSpeedOfSound(value);
        }
        public DistanceModel DistanceModel
        {
            get => GetDistanceModel();
            set => SetDistanceModel(value);
        }

        public Vector3 Position
        {
            get => GetPosition();
            set => SetPosition(value);
        }
        public Vector3 Velocity
        {
            get => GetVelocity();
            set => SetVelocity(value);
        }

        public Vector3 Up
        {
            get
            {
                GetOrientation(out _, out Vector3 up);
                return up;
            }
            set => SetOrientation(Forward, value);
        }

        public Vector3 Forward
        {
            get
            {
                GetOrientation(out Vector3 forward, out _);
                return forward;
            }
            set => SetOrientation(value, Up);
        }

        private float _gain = 1.0f;
        public float Gain
        {
            get => _gain;
            set => SetField(ref _gain, value);
        }

        private bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        private float _gainScale = 1.0f;
        public float GainScale
        {
            get => _gainScale;
            set => SetField(ref _gainScale, value);
        }

        private float? _fadeInSeconds = null;
        /// <summary>
        /// If set to a non-null value, the listener will update GainScale over this duration.
        /// </summary>
        public float? FadeInSeconds
        {
            get => _fadeInSeconds;
            set => SetField(ref _fadeInSeconds, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Gain):
                case nameof(GainScale):
                case nameof(Enabled):
                    UpdateGain();
                    break;
            }
        }

        private void UpdateGain()
            => SetGain(Gain * GainScale * (Enabled ? 1.0f : 0.0f));

        private void SetPosition(Vector3 position)
        {
            MakeCurrent();
            Api.SetListenerProperty(ListenerVector3.Position, position);
            VerifyError();
        }
        private void SetVelocity(Vector3 velocity)
        {
            MakeCurrent();
            Api.SetListenerProperty(ListenerVector3.Velocity, velocity);
            VerifyError();
        }

        private Vector3 GetPosition()
        {
            MakeCurrent();
            Api.GetListenerProperty(ListenerVector3.Position, out Vector3 position);
            VerifyError();
            return position;
        }
        private Vector3 GetVelocity()
        {
            MakeCurrent();
            Api.GetListenerProperty(ListenerVector3.Velocity, out Vector3 velocity);
            VerifyError();
            return velocity;
        }

        /// <summary>
        /// Gets both the forward and up vectors of the listener.
        /// </summary>
        /// <param name="forward"></param>
        /// <param name="up"></param>
        public unsafe void SetOrientation(Vector3 forward, Vector3 up)
        {
            MakeCurrent();
            float[] orientation = [forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z];
            fixed (float* pOrientation = orientation)
                Api.SetListenerProperty(ListenerFloatArray.Orientation, pOrientation);
            VerifyError();
        }

        /// <summary>
        /// Sets both the forward and up vectors of the listener.
        /// </summary>
        /// <param name="forward"></param>
        /// <param name="up"></param>
        public unsafe void GetOrientation(out Vector3 forward, out Vector3 up)
        {
            MakeCurrent();
            float[] orientation = new float[6];
            fixed (float* pOrientation = orientation)
                Api.GetListenerProperty(ListenerFloatArray.Orientation, pOrientation);
            VerifyError();
            forward = new Vector3(orientation[0], orientation[1], orientation[2]);
            up = new Vector3(orientation[3], orientation[4], orientation[5]);
        }

        private void SetGain(float gain)
        {
            MakeCurrent();
            Api.SetListenerProperty(ListenerFloat.Gain, gain);
            VerifyError();
        }
        private float GetGain()
        {
            MakeCurrent();
            Api.GetListenerProperty(ListenerFloat.Gain, out float gain);
            VerifyError();
            return gain;
        }

        private float GetDopplerFactor()
        {
            MakeCurrent();
            var factor = Api.GetStateProperty(StateFloat.DopplerFactor);
            VerifyError();
            return factor;
        }
        private float GetSpeedOfSound()
        {
            MakeCurrent();
            var speed = Api.GetStateProperty(StateFloat.SpeedOfSound);
            VerifyError();
            return speed;
        }
        private DistanceModel GetDistanceModel()
        {
            MakeCurrent();
            var model = (DistanceModel)Api.GetStateProperty(StateInteger.DistanceModel);
            VerifyError();
            return model;
        }

        private void SetDopplerFactor(float factor)
        {
            MakeCurrent();
            Api.DopplerFactor(factor);
            VerifyError();
        }
        private void SetSpeedOfSound(float speed)
        {
            MakeCurrent();
            Api.SpeedOfSound(speed);
            VerifyError();
        }
        private void SetDistanceModel(DistanceModel model)
        {
            MakeCurrent();
            Api.DistanceModel(model);
            VerifyError();
            _calcGainDistModelFunc = model switch
            {
                DistanceModel.InverseDistance => CalcInvDistGain,
                DistanceModel.InverseDistanceClamped => CalcInvDistGainClamped,
                DistanceModel.LinearDistance => CalcLinearGain,
                DistanceModel.LinearDistanceClamped => CalcLinearGainClamped,
                DistanceModel.ExponentDistance => CalcExpDistGain,
                DistanceModel.ExponentDistanceClamped => CalcExpDistGainClamped,
                _ => null,
            };
        }

        public event Action<ListenerContext>? Disposed;

        public void Dispose()
        {
            foreach (AudioSource source in Sources.Values)
                source.Dispose();
            foreach (AudioBuffer buffer in Buffers.Values)
                buffer.Dispose();
            Sources.Clear();
            Buffers.Clear();
            SourcePool.Destroy(int.MaxValue);
            BufferPool.Destroy(int.MaxValue);
            Disposed?.Invoke(this);
            GC.SuppressFinalize(this);
        }

        private delegate float DelCalcGainDistModel(float distance, float referenceDistance, float maxDistance, float rolloffFactor);
        private DelCalcGainDistModel? _calcGainDistModelFunc = null;

        public float CalcGain(Vector3 worldPosition, float referenceDistance, float maxDistance, float rolloffFactor)
            => _calcGainDistModelFunc?.Invoke(Vector3.Distance(worldPosition, Position), referenceDistance, maxDistance, rolloffFactor) ?? 1.0f;

        private static float ClampDist(float dist, float refDist, float maxDist)
            => Math.Max(refDist, Math.Min(dist, maxDist));

        private static float CalcExpDistGainClamped(float dist, float refDist, float maxDist, float rolloff)
            => CalcExpDistGain(ClampDist(dist, refDist, maxDist), refDist, maxDist, rolloff);
        private static float CalcExpDistGain(float dist, float refDist, float maxDist, float rolloff)
            => MathF.Pow(dist / refDist, -rolloff);

        private static float CalcLinearGainClamped(float dist, float refDist, float maxDist, float rolloff)
            => CalcLinearGain(ClampDist(dist, refDist, maxDist), refDist, maxDist, rolloff);
        private static float CalcLinearGain(float dist, float refDist, float maxDist, float rolloff)
            => 1.0f - rolloff * (dist - refDist) / (maxDist - refDist);

        private static float CalcInvDistGainClamped(float dist, float refDist, float maxDist, float rolloff)
            => CalcInvDistGain(ClampDist(dist, refDist, maxDist), refDist, maxDist, rolloff);
        private static float CalcInvDistGain(float dist, float refDist, float maxDist, float rolloff)
            => refDist / (refDist + rolloff * (dist - refDist));

        public void Tick(float deltaTime)
        {
            FadeGain(deltaTime);
        }

        public XREvent<ListenerContext>? FadeCompleted { get; set; } = null;

        private void FadeGain(float deltaTime)
        {
            if (!FadeInSeconds.HasValue)
                return;
            
            float fadeDt = deltaTime / FadeInSeconds.Value;
            float gainScale = GainScale + fadeDt;

            if (gainScale >= 1.0f)
            {
                GainScale = 1.0f;
                FadeInSeconds = null; // Stop fading
                FadeCompleted?.Invoke(this);
            }
            else if (gainScale <= 0.0f)
            {
                GainScale = 0.0f;
                FadeInSeconds = null; // Stop fading
                FadeCompleted?.Invoke(this);
            }
            else
                GainScale = gainScale;
        }
    }
}