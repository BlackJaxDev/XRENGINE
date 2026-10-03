using XREngine.Extensions;
using System.Numerics;
using XREngine.Core;
using XREngine.Data.Core;

namespace XREngine.Audio
{
    public sealed class ListenerContext : XRBase, IDisposable
    {
        //TODO: implement audio source priority
        //destroy sources with lower priority first to make room for higher priority sources.
        //0 is the lowest priority, 255 is the highest priority.

        public string? Name { get; set; }

        /// <summary>
        /// Whether this listener was created with the V2 transport/effects architecture.
        /// When true, delegates to <see cref="Transport"/> and <see cref="EffectsProcessor"/>.
        /// When false, preserves legacy OpenAL behavior through the spatial listener backend.
        /// </summary>
        public bool IsV2 { get; }

        // --- V2 path: transport/effects composition ---

        /// <summary>
        /// The public audio output transport for composed listener mode.
        /// </summary>
        public IAudioTransport? Transport { get; }

        /// <summary>The device used by both listener architectures.</summary>
        internal IAudioTransport ActiveTransport { get; }
        private IAudioListenerBackend? ListenerBackend => ActiveTransport as IAudioListenerBackend;
        private IAudioListenerBackend LegacyBackend => ListenerBackend
            ?? throw new InvalidOperationException("The listener transport does not support legacy spatial controls.");

        /// <summary>
        /// The audio effects processor. Non-null only in V2 mode (may still be null if passthrough).
        /// </summary>
        public IAudioEffectsProcessor? EffectsProcessor { get; }

        public IAudioSourceEffects? Effects { get; }

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
            ActiveTransport = transport;
            EffectsProcessor = effectsProcessor;

            if (transport is IAudioListenerBackend backend)
            {
                _position = backend.GetListenerPosition();
                _velocity = backend.GetListenerVelocity();
                backend.GetListenerOrientation(out _forward, out _up);
            }

            _gain = ListenerBackend?.GetListenerGain() ?? 1.0f;

            if (transport is IAudioSourceEffectsProvider effectsProvider)
                Effects = effectsProvider.CreateSourceEffects(this);
            if (effectsProcessor is IAudioSourceEffectsConsumer effectsConsumer)
                effectsConsumer.SetSourceEffects(Effects);

            SourcePool = new ResourcePool<AudioSource>(() => new AudioSource(this));
            BufferPool = new ResourcePool<AudioBuffer>(() => new AudioBuffer(this));

            EffectsProcessor?.Initialize(new AudioEffectsSettings { SampleRate = Transport.SampleRate });

            AudioDiagnostics.RecordListenerCreated(Name);
        }

        /// <summary>
        /// Legacy constructor: OpenAL listener semantics through the registered backend.
        /// Used when <see cref="AudioSettings.AudioArchitectureV2"/> is disabled (default).
        /// </summary>
        internal ListenerContext()
        {
            IsV2 = false;
            IAudioTransport transport = AudioBackendRegistry.CreateTransport(EAudioTransport.OpenAL);
            if (transport is not IAudioListenerBackend backend)
            {
                transport.Dispose();
                throw new InvalidOperationException("The registered OpenAL transport does not support legacy listener controls.");
            }

            ActiveTransport = transport;

            try
            {
                if (transport is IAudioSourceEffectsProvider effectsProvider)
                    Effects = effectsProvider.CreateSourceEffects(this);
                _gain = backend.GetListenerGain();
            }
            catch
            {
                transport.Dispose();
                throw;
            }

            SourcePool = new ResourcePool<AudioSource>(() => new AudioSource(this));
            BufferPool = new ResourcePool<AudioBuffer>(() => new AudioBuffer(this));

            AudioDiagnostics.RecordListenerCreated(Name);
        }

        public static ListenerContext? CurrentContext { get; private set; }

        public void MakeCurrent()
        {
            if (ActiveTransport is IAudioListenerBackend backend)
            {
                backend.MakeCurrent();
                CurrentContext = this;
            }
        }

        public void VerifyError()
        {
            if (ActiveTransport is IAudioListenerBackend backend)
                backend.VerifyError();
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
            if (!ReferenceEquals(source.ParentListener, this))
                throw new InvalidOperationException("A source must be released to its owning listener.");
            if (source.Handle != 0)
                Sources.Remove(source.Handle);
            SourcePool.Release(source);
            VerifyError();
        }
        public void ReleaseBuffer(AudioBuffer buffer)
        {
            if (buffer is null)
                return;
            if (!ReferenceEquals(buffer.ParentListener, this))
                throw new InvalidOperationException("A buffer must be released to its owning listener.");
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
            return ListenerBackend?.IsExtensionPresent(extension) ?? false;
        }

        public bool HasDopplerFactorSet()
            => ListenerBackend?.HasDopplerFactorSet() ?? true;
        public bool HasDopplerVelocitySet()
            => ListenerBackend?.HasDopplerVelocitySet() ?? true;
        public bool HasSpeedOfSoundSet()
            => ListenerBackend?.HasSpeedOfSoundSet() ?? true;
        public bool IsDistanceModelInverseDistanceClamped()
            => ListenerBackend?.IsDistanceModelInverseDistanceClamped()
                ?? DistanceModel == EDistanceModel.InverseDistanceClamped;

        public string GetVendor()
            => ListenerBackend?.GetVendor() ?? ActiveTransport.GetType().Name;
        public string GetRenderer()
            => ListenerBackend?.GetRenderer() ?? ActiveTransport.GetType().Name;
        public string GetVersion()
            => ListenerBackend?.GetVersion() ?? "V2";
        public string[] GetExtensions()
            => ListenerBackend?.GetExtensions() ?? [];

        private float _dopplerFactor = 1.0f;
        private float _speedOfSound = 343.3f;
        private EDistanceModel _distanceModel = EDistanceModel.InverseDistanceClamped;

        public float DopplerFactor
        {
            get
            {
                if (IsV2 && ListenerBackend is { } backend)
                    return _dopplerFactor = backend.GetDopplerFactor();
                if (IsV2)
                    return _dopplerFactor;
                return GetDopplerFactor();
            }
            set
            {
                if (IsV2 && ListenerBackend is { } backend)
                    backend.SetDopplerFactor(value);
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
                if (IsV2 && ListenerBackend is { } backend)
                    return _speedOfSound = backend.GetSpeedOfSound();
                if (IsV2)
                    return _speedOfSound;
                return GetSpeedOfSound();
            }
            set
            {
                if (IsV2 && ListenerBackend is { } backend)
                    backend.SetSpeedOfSound(value);
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
                if (IsV2 && ListenerBackend is { } backend)
                    return _position = backend.GetListenerPosition();
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
                if (IsV2 && ListenerBackend is { } backend)
                    return _velocity = backend.GetListenerVelocity();
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
                if (IsV2 && ListenerBackend is { } backend)
                {
                    backend.GetListenerOrientation(out _, out Vector3 up);
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
                if (IsV2 && ListenerBackend is { } backend)
                {
                    backend.GetListenerOrientation(out Vector3 forward, out _);
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
            => LegacyBackend.SetListenerPosition(position);
        private void SetVelocity(Vector3 velocity)
            => LegacyBackend.SetListenerVelocity(velocity);

        private Vector3 GetPosition()
            => LegacyBackend.GetListenerPosition();
        private Vector3 GetVelocity()
            => LegacyBackend.GetListenerVelocity();

        /// <summary>
        /// Gets both the forward and up vectors of the listener.
        /// </summary>
        /// <param name="forward"></param>
        /// <param name="up"></param>
        public void SetOrientation(Vector3 forward, Vector3 up)
        {
            if (IsV2)
            {
                _forward = forward;
                _up = up;
                Transport!.SetListenerOrientation(forward, up);
                EffectsProcessor?.SetListenerPose(Position, forward, up);
                return;
            }

            LegacyBackend.SetListenerOrientation(forward, up);
        }

        /// <summary>
        /// Sets both the forward and up vectors of the listener.
        /// </summary>
        /// <param name="forward"></param>
        /// <param name="up"></param>
        public void GetOrientation(out Vector3 forward, out Vector3 up)
        {
            if (IsV2)
            {
                if (ListenerBackend is { } backend)
                {
                    backend.GetListenerOrientation(out forward, out up);
                    _forward = forward;
                    _up = up;
                    return;
                }

                forward = _forward;
                up = _up;
                return;
            }

            LegacyBackend.GetListenerOrientation(out forward, out up);
        }

        private void SetGain(float gain)
            => LegacyBackend.SetListenerGain(gain);
        private float GetGain()
            => LegacyBackend.GetListenerGain();

        private float GetDopplerFactor()
            => LegacyBackend.GetDopplerFactor();
        private float GetSpeedOfSound()
            => LegacyBackend.GetSpeedOfSound();

        private void SetDopplerFactor(float factor)
            => LegacyBackend.SetDopplerFactor(factor);
        private void SetSpeedOfSound(float speed)
            => LegacyBackend.SetSpeedOfSound(speed);
        private void UpdateDistanceGainCalculation(EDistanceModel model)
        {
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

        // --- EDistanceModel adapter methods for V2 path ---

        private EDistanceModel GetDistanceModelV2()
        {
            if (ListenerBackend is { } backend)
                return _distanceModel = backend.GetDistanceModel();
            return _distanceModel;
        }

        private void SetDistanceModelV2(EDistanceModel model)
        {
            _distanceModel = model;
            ListenerBackend?.SetDistanceModel(model);
            UpdateDistanceGainCalculation(model);
        }

        private EDistanceModel GetDistanceModelLegacy()
            => LegacyBackend.GetDistanceModel();

        private void SetDistanceModelLegacy(EDistanceModel model)
        {
            LegacyBackend.SetDistanceModel(model);
            UpdateDistanceGainCalculation(model);
        }

        public event Action<ListenerContext>? Disposed;

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

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
            Effects?.Dispose();
            ActiveTransport.Dispose();
            if (CurrentContext == this)
                CurrentContext = null;

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
