using XREngine.Extensions;
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

        /// <summary>
        /// Whether this listener was created with the V2 transport/effects architecture.
        /// When true, delegates to <see cref="Transport"/> and <see cref="EffectsProcessor"/>.
        /// When false, uses the legacy monolithic OpenAL code path.
        /// </summary>
        public bool IsV2 { get; }

        // --- V2 path: transport/effects composition ---

        /// <summary>
        /// The audio output transport layer. Non-null only in V2 mode.
        /// </summary>
        public IAudioTransport? Transport { get; }

        /// <summary>
        /// The audio effects processor. Non-null only in V2 mode (may still be null if passthrough).
        /// </summary>
        public IAudioEffectsProcessor? EffectsProcessor { get; }

        // --- Legacy path: direct OpenAL objects ---

        public AL Api { get; }
        public ALContext Context { get; }

        internal Device* DeviceHandle { get; }
        internal Context* ContextHandle { get; }

        public EffectContext? Effects { get; }
        public VorbisFormat? VorbisFormat { get; }
        public MP3Format? MP3Format { get; }
        public XRam? XRam { get; }
        public MultiChannelBuffers? MultiChannel { get; }
        public DoubleFormat? DoubleFormat { get; }
        public MULAWFormat? MuLawFormat { get; }
        public FloatFormat? FloatFormat { get; }
        public MCFormats? MCFormats { get; }
        public ALAWFormat? ALawFormat { get; }

        public Capture? Capture { get; }

        public EventDictionary<uint, AudioSource> Sources { get; } = [];
        public EventDictionary<uint, AudioBuffer> Buffers { get; } = [];

        private Vector3 _position = Vector3.Zero;
        private Vector3 _velocity = Vector3.Zero;
        private Vector3 _forward = -Vector3.UnitZ;
        private Vector3 _up = Vector3.UnitY;

        /// <summary>
        /// V2 constructor: composes a transport and effects processor.
        /// Used when <see cref="AudioSettings.AudioArchitectureV2"/> is enabled.
        /// </summary>
        internal ListenerContext(IAudioTransport transport, IAudioEffectsProcessor? effectsProcessor)
        {
            IsV2 = true;
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            EffectsProcessor = effectsProcessor;

            // Bridge: expose OpenAL internals for code that still needs direct access
            // (AudioSource EFX properties, AudioInputDevice capture, format extensions, etc.)
            if (transport is OpenALTransport oalTransport)
            {
                Api = oalTransport.Api;
                Context = oalTransport.Context;
                DeviceHandle = oalTransport.DeviceHandle;
                ContextHandle = oalTransport.ContextHandle;
                VorbisFormat = oalTransport.VorbisFormat;
                MP3Format = oalTransport.MP3Format;
                MultiChannel = oalTransport.MultiChannel;
                DoubleFormat = oalTransport.DoubleFormat;
                MuLawFormat = oalTransport.MuLawFormat;
                FloatFormat = oalTransport.FloatFormat;
                MCFormats = oalTransport.MCFormats;
                ALawFormat = oalTransport.ALawFormat;
                XRam = oalTransport.XRam;
                Capture = null; // Capture deferred to transport abstraction in later phase

                // Wire up EFX processor's EffectContext
                if (effectsProcessor is OpenALEfxProcessor efxProc)
                {
                    efxProc.SetListenerContext(this);
                    Effects = efxProc.EffectContext;
                }

                _position = oalTransport.GetListenerPosition();
                _velocity = oalTransport.GetListenerVelocity();
                oalTransport.GetListenerOrientation(out _forward, out _up);
            }
            else
            {
                // Non-OpenAL transport: OpenAL fields are default/null
                Api = AL.GetApi(); // Needed to avoid null; won't be used
                Context = ALContext.GetApi(false);
                DeviceHandle = null;
                ContextHandle = null;
            }

            _gain = transport is OpenALTransport oalt ? oalt.GetListenerGain() : 1.0f;

            SourcePool = new ResourcePool<AudioSource>(() => new AudioSource(this));
            BufferPool = new ResourcePool<AudioBuffer>(() => new AudioBuffer(this));

            EffectsProcessor?.Initialize(new AudioEffectsSettings { SampleRate = Transport.SampleRate });

            AudioDiagnostics.RecordListenerCreated(Name);
        }

        /// <summary>
        /// Legacy constructor: monolithic OpenAL path (original behavior).
        /// Used when <see cref="AudioSettings.AudioArchitectureV2"/> is disabled (default).
        /// </summary>
        internal ListenerContext()
        {
            IsV2 = false;
            Api = AL.GetApi();

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

            DeviceHandle = Context.OpenDevice(null);
            ContextHandle = Context.CreateContext(DeviceHandle, null);
            MakeCurrent();
            VerifyError();

            _gain = GetGain();

            SourcePool = new ResourcePool<AudioSource>(() => new AudioSource(this));
            BufferPool = new ResourcePool<AudioBuffer>(() => new AudioBuffer(this));

            AudioDiagnostics.RecordListenerCreated(Name);
        }

        public static ListenerContext? CurrentContext { get; private set; }

        public void MakeCurrent()
        {
            if (IsV2 && Transport is OpenALTransport oalTransport)
            {
                oalTransport.MakeCurrent();
                CurrentContext = this;
                return;
            }

            if (IsV2)
                return;

            if (CurrentContext == this)
                return;

            CurrentContext = this;
            Context.MakeContextCurrent(ContextHandle);
        }

        public void VerifyError()
        {
            if (IsV2 && Transport is OpenALTransport oalTransport)
            {
                oalTransport.VerifyError();
                return;
            }

            if (IsV2)
                return;

            if (CurrentContext != this)
                return;

            var error = Api.GetError();
            if (error != AudioError.NoError)
            {
                Trace.WriteLine($"OpenAL Error: {error}");
                AudioDiagnostics.RecordOpenALError($"{error}");
            }
        }

        private ResourcePool<AudioSource> SourcePool { get; }
        private ResourcePool<AudioBuffer> BufferPool { get; }

        public AudioSource TakeSource()
        {
            var source = SourcePool.Take();
            if (source.Handle != 0)
                Sources.Add(source.Handle, source);
            VerifyError();
            return source;
        }
        public AudioBuffer TakeBuffer()
        {
            var buffer = BufferPool.Take();
            if (buffer.Handle != 0)
                Buffers.Add(buffer.Handle, buffer);
            VerifyError();
            return buffer;
        }

        public void ReleaseSource(AudioSource source)
        {
            if (source is null)
                return;
            if (source.Handle != 0)
                Sources.Remove(source.Handle);
            SourcePool.Release(source);
            VerifyError();
        }
        public void ReleaseBuffer(AudioBuffer buffer)
        {
            if (buffer is null)
                return;
            if (buffer.Handle != 0)
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
        {
            if (IsV2 && Transport is not OpenALTransport)
                return false;
            return Api.IsExtensionPresent(extension);
        }

        public bool HasDopplerFactorSet()
            => IsV2 && Transport is not OpenALTransport
                ? true
                : Api.GetStateProperty(StateBoolean.HasDopplerFactor);
        public bool HasDopplerVelocitySet()
            => IsV2 && Transport is not OpenALTransport
                ? true
                : Api.GetStateProperty(StateBoolean.HasDopplerVelocity);
        public bool HasSpeedOfSoundSet()
            => IsV2 && Transport is not OpenALTransport
                ? true
                : Api.GetStateProperty(StateBoolean.HasSpeedOfSound);
        public bool IsDistanceModelInverseDistanceClamped()
            => IsV2 && Transport is not OpenALTransport
                ? DistanceModel == EDistanceModel.InverseDistanceClamped
                : Api.GetStateProperty(StateBoolean.IsDistanceModelInverseDistanceClamped);

        public string GetVendor()
            => IsV2 && Transport is not OpenALTransport
                ? Transport?.GetType().Name ?? "UnknownTransport"
                : Api.GetStateProperty(StateString.Vendor);
        public string GetRenderer()
            => IsV2 && Transport is not OpenALTransport
                ? Transport?.GetType().Name ?? "UnknownTransport"
                : Api.GetStateProperty(StateString.Renderer);
        public string GetVersion()
            => IsV2 && Transport is not OpenALTransport
                ? "V2"
                : Api.GetStateProperty(StateString.Version);
        public string[] GetExtensions()
            => IsV2 && Transport is not OpenALTransport
                ? []
                : Api.GetStateProperty(StateString.Extensions).Split(' ');

        private float _dopplerFactor = 1.0f;
        private float _speedOfSound = 343.3f;
        private EDistanceModel _distanceModel = EDistanceModel.InverseDistanceClamped;

        public float DopplerFactor
        {
            get
            {
                if (IsV2 && Transport is OpenALTransport oal)
                    return _dopplerFactor = oal.GetDopplerFactor();
                if (IsV2)
                    return _dopplerFactor;
                return GetDopplerFactor();
            }
            set
            {
                if (IsV2 && Transport is OpenALTransport oal)
                    oal.SetDopplerFactor(value);
                else if (IsV2)
                    _dopplerFactor = value;
                else
                    SetDopplerFactor(value);
            }
        }
        public float SpeedOfSound
        {
            get
            {
                if (IsV2 && Transport is OpenALTransport oal)
                    return _speedOfSound = oal.GetSpeedOfSound();
                if (IsV2)
                    return _speedOfSound;
                return GetSpeedOfSound();
            }
            set
            {
                if (IsV2 && Transport is OpenALTransport oal)
                    oal.SetSpeedOfSound(value);
                else if (IsV2)
                    _speedOfSound = value;
                else
                    SetSpeedOfSound(value);
            }
        }
        public EDistanceModel DistanceModel
        {
            get => IsV2 ? GetDistanceModelV2() : GetDistanceModelLegacy();
            set
            {
                if (IsV2)
                    SetDistanceModelV2(value);
                else
                    SetDistanceModelLegacy(value);
            }
        }

        public Vector3 Position
        {
            get
            {
                if (IsV2 && Transport is OpenALTransport oal)
                    return _position = oal.GetListenerPosition();
                if (IsV2)
                    return _position;
                return GetPosition();
            }
            set
            {
                _position = value;
                if (IsV2)
                    Transport!.SetListenerPosition(value);
                else
                    SetPosition(value);
            }
        }
        public Vector3 Velocity
        {
            get
            {
                if (IsV2 && Transport is OpenALTransport oal)
                    return _velocity = oal.GetListenerVelocity();
                if (IsV2)
                    return _velocity;
                return GetVelocity();
            }
            set
            {
                _velocity = value;
                if (IsV2)
                    Transport!.SetListenerVelocity(value);
                else
                    SetVelocity(value);
            }
        }

        public Vector3 Up
        {
            get
            {
                if (IsV2 && Transport is OpenALTransport oal)
                {
                    oal.GetListenerOrientation(out _, out Vector3 up);
                    _up = up;
                    return up;
                }
                if (IsV2)
                    return _up;
                GetOrientation(out _, out Vector3 upLegacy);
                return upLegacy;
            }
            set => SetOrientation(Forward, value);
        }

        public Vector3 Forward
        {
            get
            {
                if (IsV2 && Transport is OpenALTransport oal)
                {
                    oal.GetListenerOrientation(out Vector3 forward, out _);
                    _forward = forward;
                    return forward;
                }
                if (IsV2)
                    return _forward;
                GetOrientation(out Vector3 forwardLegacy, out _);
                return forwardLegacy;
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
        {
            float effectiveGain = Gain * GainScale * (Enabled ? 1.0f : 0.0f);
            if (IsV2)
                Transport!.SetListenerGain(effectiveGain);
            else
                SetGain(effectiveGain);
        }

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
            if (IsV2)
            {
                _forward = forward;
                _up = up;
                Transport!.SetListenerOrientation(forward, up);
                EffectsProcessor?.SetListenerPose(Position, forward, up);
                return;
            }

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
            if (IsV2)
            {
                if (Transport is OpenALTransport oal)
                {
                    oal.GetListenerOrientation(out forward, out up);
                    _forward = forward;
                    _up = up;
                    return;
                }

                forward = _forward;
                up = _up;
                return;
            }

            MakeCurrent();
            float* orientation = stackalloc float[6];
            Api.GetListenerProperty(ListenerFloatArray.Orientation, orientation);
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
        private Silk.NET.OpenAL.DistanceModel GetDistanceModel()
        {
            MakeCurrent();
            var model = (Silk.NET.OpenAL.DistanceModel)Api.GetStateProperty(StateInteger.DistanceModel);
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
        private void SetDistanceModel(Silk.NET.OpenAL.DistanceModel model)
        {
            MakeCurrent();
            Api.DistanceModel(model);
            VerifyError();
            _calcGainDistModelFunc = model switch
            {
                Silk.NET.OpenAL.DistanceModel.InverseDistance => CalcInvDistGain,
                Silk.NET.OpenAL.DistanceModel.InverseDistanceClamped => CalcInvDistGainClamped,
                Silk.NET.OpenAL.DistanceModel.LinearDistance => CalcLinearGain,
                Silk.NET.OpenAL.DistanceModel.LinearDistanceClamped => CalcLinearGainClamped,
                Silk.NET.OpenAL.DistanceModel.ExponentDistance => CalcExpDistGain,
                Silk.NET.OpenAL.DistanceModel.ExponentDistanceClamped => CalcExpDistGainClamped,
                _ => null,
            };
        }

        // --- EDistanceModel adapter methods for V2 path ---

        private EDistanceModel GetDistanceModelV2()
        {
            if (Transport is OpenALTransport oal)
                return _distanceModel = (EDistanceModel)(int)oal.GetDistanceModel();
            return _distanceModel;
        }

        private void SetDistanceModelV2(EDistanceModel model)
        {
            _distanceModel = model;
            if (Transport is OpenALTransport oal)
                oal.SetDistanceModel((DistanceModel)(int)model);

            _calcGainDistModelFunc = model switch
            {
                EDistanceModel.InverseDistance => CalcInvDistGain,
                EDistanceModel.InverseDistanceClamped => CalcInvDistGainClamped,
                EDistanceModel.LinearDistance => CalcLinearGain,
                EDistanceModel.LinearDistanceClamped => CalcLinearGainClamped,
                EDistanceModel.ExponentDistance => CalcExpDistGain,
                EDistanceModel.ExponentDistanceClamped => CalcExpDistGainClamped,
                _ => null,
            };
        }

        private EDistanceModel GetDistanceModelLegacy()
            => (EDistanceModel)(int)GetDistanceModel();

        private void SetDistanceModelLegacy(EDistanceModel model)
            => SetDistanceModel((DistanceModel)(int)model);

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

            // Dispose V2 resources
            EffectsProcessor?.Dispose();
            Transport?.Dispose();

            AudioDiagnostics.RecordListenerDisposed(Name);
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
            EffectsProcessor?.Tick(deltaTime);
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