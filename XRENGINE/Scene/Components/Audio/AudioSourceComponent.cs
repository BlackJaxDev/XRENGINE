using Extensions;
using MathNet.Numerics;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Audio;
using XREngine.Data;
using XREngine.Data.Core;
using static XREngine.Audio.AudioSource;

namespace XREngine.Components.Scene
{
    public class AudioSourceComponent : XRComponent
    {
        private ESourceState _state = ESourceState.Initial;
        private float _rolloffFactor = 1.0f;
        private float _referenceDistance = 1.0f;
        private float _maxDistance = float.PositiveInfinity;
        private AudioData? _staticBuffer;
        private bool _relativeToListener = false;
        private bool _looping = false;
        private float _pitch = 1.0f;
        private float _minGain = 0.0f;
        private float _maxGain = 1.0f;
        private float _gain = 1.0f;
        private float _coneInnerAngle = 360.0f;
        private float _coneOuterAngle = 360.0f;
        private float _coneOuterGain = 0.0f;

        /// <summary>
        /// These are the listeners that are currently listening to this audio source because it is within their range.
        /// </summary>
        public ConcurrentDictionary<ListenerContext, AudioSource> ActiveListeners { get; set; } = [];

        /// <summary>
        /// The rolloff factor of the source.
        /// Rolloff factor is the rate at which the source's volume decreases as it moves further from the listener.
        /// Range: [0.0f - float.PositiveInfinity]
        /// </summary>
        public float RolloffFactor
        {
            get => _rolloffFactor;
            set => SetField(ref _rolloffFactor, value);
        }
        /// <summary>
        /// How far the source is from the listener.
        /// At 0.0f, no distance attenuation occurs.
        /// Default: 1.0f.
        /// Range: [0.0f - float.PositiveInfinity] 
        /// </summary>
        public float ReferenceDistance
        {
            get => _referenceDistance;
            set => SetField(ref _referenceDistance, value);
        }
        /// <summary>
        /// The distance above which sources are not attenuated using the inverse clamped distance model.
        /// Default: float.PositiveInfinity
        /// Range: [0.0f - float.PositiveInfinity]
        /// </summary>
        public float MaxDistance
        {
            get => _maxDistance;
            set => SetField(ref _maxDistance, value);
        }
        /// <summary>
        /// The current playback state of the source.
        /// </summary>
        public ESourceState State
        {
            get => _state;
            private set => SetField(ref _state, value);
        }
        /// <summary>
        /// The type of data this source expects.
        /// </summary>
        public ESourceType Type => ActiveListeners.Values.FirstOrDefault()?.SourceType ?? ESourceType.Undetermined;
        /// <summary>
        /// If true, the source's position is relative to the listener.
        /// If false, the source's position is in world space.
        /// </summary>
        public bool RelativeToListener
        {
            get => _relativeToListener;
            set => SetField(ref _relativeToListener, value);
        }
        /// <summary>
        /// If true, the source will loop.
        /// </summary>
        public bool Loop
        {
            get => _looping;
            set => SetField(ref _looping, value);
        }
        /// <summary>
        /// The pitch of the source.
        /// Default: 1.0f
        /// Range: [0.5f - 2.0f]
        /// </summary>
        public float Pitch
        {
            get => _pitch;
            set => SetField(ref _pitch, value);
        }
        /// <summary>
        /// The minimum gain of the source.
        /// Range: [0.0f - 1.0f] (Logarithmic)
        /// </summary>
        public float MinGain
        {
            get => _minGain;
            set => SetField(ref _minGain, value);
        }
        /// <summary>
        /// The maximum gain of the source.
        /// Range: [0.0f - 1.0f] (Logarithmic)
        /// </summary>
        public float MaxGain
        {
            get => _maxGain;
            set => SetField(ref _maxGain, value);
        }
        /// <summary>
        /// The gain (volume) of the source.
        /// A value of 1.0 means un-attenuated/unchanged.
        /// Each division by 2 equals an attenuation of -6dB.
        /// Each multiplication with 2 equals an amplification of +6dB.
        /// A value of 0.0f is meaningless with respect to a logarithmic scale; it is interpreted as zero volume - the channel is effectively disabled.
        /// </summary>
        public float Gain
        {
            get => _gain;
            set => SetField(ref _gain, value);
        }
        /// <summary>
        /// Directional source, inner cone angle, in degrees.
        /// Default: 360
        /// Range: [0-360]
        /// </summary>
        public float ConeInnerAngle
        {
            get => _coneInnerAngle;
            set => SetField(ref _coneInnerAngle, value);
        }
        /// <summary>
        /// Directional source, outer cone angle, in degrees.
        /// Default: 360
        /// Range: [0-360]
        /// </summary>
        public float ConeOuterAngle
        {
            get => _coneOuterAngle;
            set => SetField(ref _coneOuterAngle, value);
        }
        /// <summary>
        /// Directional source, outer cone gain.
        /// Default: 0.0f
        /// Range: [0.0f - 1.0] (Logarithmic)
        /// </summary>
        public float ConeOuterGain
        {
            get => _coneOuterGain;
            set => SetField(ref _coneOuterGain, value);
        }

        /// <summary>
        /// Plays the audio source.
        /// </summary>
        public void Play()
        {
            //if (State == ESourceState.Playing)
            //    return;

            State = ESourceState.Playing;
        }
        /// <summary>
        /// Stops the audio source.
        /// </summary>
        public void Stop()
        {
            //if (State != ESourceState.Playing)
            //    return;

            State = ESourceState.Stopped;
        }
        /// <summary>
        /// Pauses the audio source.
        /// </summary>
        public void Pause()
        {
            //if (State != ESourceState.Playing)
            //    return;

            State = ESourceState.Paused;
        }
        /// <summary>
        /// Rewinds the audio source to the beginning.
        /// </summary>
        public void Rewind()
        {
            //if (State == ESourceState.Initial)
            //    return;

            State = ESourceState.Initial;
        }

        public AudioData? StaticBuffer
        {
            get => _staticBuffer;
            set
            {
                //if (Type == ESourceType.Streaming)
                //    throw new InvalidOperationException("Cannot set static buffer on a streaming source.");

                SetField(ref _staticBuffer, value);
            }
        }

        public void SetStaticBuffer(AudioBuffer buffer)
        {
            //if (Type == ESourceType.Streaming)
            //    throw new InvalidOperationException("Cannot set static buffer on a streaming source.");

            //lock (ActiveListeners)
            //{
                foreach (var source in ActiveListeners.Values)
                    source.Buffer = buffer;
            //}
        }

        private int _maxStreamingBuffers = 10;
        public int MaxStreamingBuffers
        {
            get => _maxStreamingBuffers;
            set => SetField(ref _maxStreamingBuffers, value);
        }

        public XREvent<(int frequency, bool stereo, float[] buffer)>? StreamingBufferEnqueuedFloat;
        public XREvent<(int frequency, bool stereo, short[] buffer)>? StreamingBufferEnqueuedShort;
        public XREvent<(int frequency, bool stereo, byte[] buffer)>? StreamingBufferEnqueuedByte;
        public XREvent<AudioData>? StreamingBufferEnqueued;
        
        public void EnqueueStreamingBuffers(int frequency, bool stereo, params float[][] buffers)
        {
            //if (Type == ESourceType.Static)
            //    throw new InvalidOperationException("Cannot queue streaming buffers on a static source.");

            //lock (ActiveListeners)
            //{
            foreach (var source in ActiveListeners.Values)
            {
                foreach (var buffer in buffers)
                {
                    var audioBuffer = source.ParentListener.TakeBuffer();
                    audioBuffer.SetData(buffer, frequency, stereo);
                    source.QueueBuffers(MaxStreamingBuffers, audioBuffer);
                    StreamingBufferEnqueuedFloat?.Invoke((frequency, stereo, buffer));
                }
            }
            //}
        }
        public void EnqueueStreamingBuffers(int frequency, bool stereo, params short[][] buffers)
        {
            //if (Type == ESourceType.Static)
            //    throw new InvalidOperationException("Cannot queue streaming buffers on a static source.");

            //lock (ActiveListeners)
            //{
            foreach (var source in ActiveListeners.Values)
            {
                foreach (var buffer in buffers)
                {
                    var audioBuffer = source.ParentListener.TakeBuffer();
                    audioBuffer.SetData(buffer, frequency, stereo);
                    source.QueueBuffers(MaxStreamingBuffers, audioBuffer);
                    StreamingBufferEnqueuedShort?.Invoke((frequency, stereo, buffer));
                }
            }
            //}
        }
        public void EnqueueStreamingBuffers(int frequency, bool stereo, params byte[][] buffers)
        {
            //if (Type == ESourceType.Static)
            //    throw new InvalidOperationException("Cannot queue streaming buffers on a static source.");

            //lock (ActiveListeners)
            //{
            foreach (var source in ActiveListeners.Values)
            {
                foreach (var buffer in buffers)
                {
                    AudioBuffer audioBuffer = source.ParentListener.TakeBuffer();
                    audioBuffer.SetData(buffer, frequency, stereo);
                    source.QueueBuffers(MaxStreamingBuffers, audioBuffer);
                    StreamingBufferEnqueuedByte?.Invoke((frequency, stereo, buffer));
                }
            }
            //}
        }
        public void EnqueueStreamingBuffers(params AudioData[] buffers)
        {
            //if (Type == ESourceType.Static)
            //    throw new InvalidOperationException("Cannot queue streaming buffers on a static source.");

            //lock (ActiveListeners)
            //{
            foreach (var source in ActiveListeners.Values)
            {
                foreach (var buffer in buffers)
                {
                    var audioBuffer = source.ParentListener.TakeBuffer();
                    audioBuffer.SetData(buffer);
                    source.QueueBuffers(MaxStreamingBuffers, audioBuffer);
                    StreamingBufferEnqueued?.Invoke(buffer);
                }
            }
            //}
        }
        public void DequeueConsumedBuffers()
        {
            if (Type != ESourceType.Streaming)
                return;

            //if (!Engine.IsRenderThread)
            //{
            //    Engine.EnqueueMainThreadTask(DequeueConsumedBuffers);
            //    return;
            //}

            //lock (ActiveListeners)
            //{
            //int min = 0;
            //int min = ActiveListeners.Values.Min(x => x.BuffersProcessed);
            //if (min == 0)
            //    return;

            //foreach (var source in ActiveListeners.Values)
            //{
            //    var buffers = source.UnqueueConsumedBuffers(0);
            //    if (buffers != null)
            //        foreach (var buffer in buffers)
            //            source.ParentListener.ReleaseBuffer(buffer);
            //}
            //}
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdatePosition);

            if (PlayOnActivate)
                Play();
        }

        private bool _playOnActivate = false;
        public bool PlayOnActivate
        {
            get => _playOnActivate;
            set => SetField(ref _playOnActivate, value);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            //lock (ActiveListeners)
            //{
            foreach (var source in ActiveListeners.Values)
                source.Dispose();
            ActiveListeners.Clear();
            //}
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(RolloffFactor):
                    //lock (ActiveListeners)
                    //{
                        foreach (var source in ActiveListeners.Values)
                            source.RolloffFactor = RolloffFactor;
                    //}
                    break;
                case nameof(ReferenceDistance):
                    //lock (ActiveListeners)
                    //{
                        foreach (var source in ActiveListeners.Values)
                            source.ReferenceDistance = ReferenceDistance;
                    //}
                    break;
                case nameof(MaxDistance):
                    //lock (ActiveListeners)
                    //{
                        foreach (var source in ActiveListeners.Values)
                            source.MaxDistance = MaxDistance;
                    //}
                    break;
                case nameof(RelativeToListener):
                    //lock (ActiveListeners)
                    //{
                        foreach (var source in ActiveListeners.Values)
                            source.RelativeToListener = RelativeToListener;
                    //}
                    break;
            case nameof(Type):
                //lock (ActiveListeners)
                //{
                //    foreach (var source in ActiveListeners.Values)
                //        source.SourceType = Type;
                //}
                break;
            case nameof(Loop):
                    //lock (ActiveListeners)
                    //{
                        foreach (var source in ActiveListeners.Values)
                            source.Looping = Loop;
                    //}
                    break;
                case nameof(Pitch):
                    //lock (ActiveListeners)
                    //{
                        foreach (var source in ActiveListeners.Values)
                            source.Pitch = Pitch;
                    //}
                    break;
                case nameof(MinGain):
                    //lock (ActiveListeners)
                    //{
                        foreach (var source in ActiveListeners.Values)
                            source.MinGain = MinGain;
                    //}
                    break;
                case nameof(MaxGain):
                    //lock (ActiveListeners)
                    //{
                        foreach (var source in ActiveListeners.Values)
                            source.MaxGain = MaxGain;
                    //}
                    break;
                case nameof(Gain):
                    //lock (ActiveListeners)
                    //{
                        foreach (var source in ActiveListeners.Values)
                            source.Gain = Gain;
                    //}
                    break;
                case nameof(ConeInnerAngle):
                    //lock (ActiveListeners)
                    //{
                        foreach (var source in ActiveListeners.Values)
                            source.ConeInnerAngle = ConeInnerAngle;
                    //}
                    break;
                case nameof(ConeOuterAngle):
                    //lock (ActiveListeners)
                    //{
                        foreach (var source in ActiveListeners.Values)
                            source.ConeOuterAngle = ConeOuterAngle;
                    //}
                    break;
                case nameof(ConeOuterGain):
                    //lock (ActiveListeners)
                    //{
                        foreach (var source in ActiveListeners.Values)
                            source.ConeOuterGain = ConeOuterGain;
                    //}
                    break;
                case nameof(State):
                    StateChanged();
                    break;
                case nameof(StaticBuffer):
                    StaticBufferChanged();
                    break;
            }
        }

        private void StateChanged()
        {
            //lock (ActiveListeners)
            //{
                switch (State)
                {
                    case ESourceState.Playing:
                        foreach (var source in ActiveListeners.Values)
                            source.Play();
                        break;
                    case ESourceState.Stopped:
                        foreach (var source in ActiveListeners.Values)
                            source.Stop();
                        break;
                    case ESourceState.Paused:
                        foreach (var source in ActiveListeners.Values)
                            source.Pause();
                        break;
                    case ESourceState.Initial:
                        foreach (var source in ActiveListeners.Values)
                            source.Rewind();
                        break;
                }
            //}
        }

        private void StateChanged(AudioSource source)
        {
            switch (State)
            {
                case ESourceState.Playing:
                    source.Play();
                    break;
                case ESourceState.Stopped:
                    source.Stop();
                    break;
                case ESourceState.Paused:
                    source.Pause();
                    break;
                case ESourceState.Initial:
                    source.Rewind();
                    break;
            }
        }

        private void StaticBufferChanged()
        {
            //lock (ActiveListeners)
            //{
                foreach (KeyValuePair<ListenerContext, AudioSource> pair in ActiveListeners)
                {
                    ListenerContext listener = pair.Key;
                    AudioSource source = pair.Value;
                    StaticBufferChanged(listener, source);
                }
            //}
        }

        private void StaticBufferChanged(ListenerContext listener, AudioSource source)
        {
            source.Stop();
            source.Rewind();

            if (_staticBuffer is not null)
            {
                if (source.Buffer is null)
                {
                    var buffer = listener.TakeBuffer();
                    buffer.SetData(_staticBuffer);
                    source.Buffer = buffer;
                }
                else
                    source.Buffer.SetData(_staticBuffer);
            }
            else if (source.Buffer is not null)
            {
                listener.ReleaseBuffer(source.Buffer);
                source.Buffer = null;
            }
        }

        private void UpdatePosition()
        {
            Vector3 worldPosition = Transform.WorldTranslation;
            UpdateValidListeners(worldPosition);
            UpdateOrientation(worldPosition);
            DequeueConsumedBuffers();
        }

        public bool IsStereo
        {
            get
            {
                return Type switch
                {
                    ESourceType.Static => StaticBuffer?.Stereo ?? false,
                    _ => false,
                };
            }
        }

        private void UpdateOrientation(Vector3 worldPosition)
        {
            if (IsStereo)
                return;

            Vector3 worldForward = Transform.WorldForward;

            float delta = Engine.Delta;
            foreach (var pair in ActiveListeners)
            {
                ListenerContext listener = pair.Key;
                AudioSource source = pair.Value;

                if (RelativeToListener)
                {
                    //Convert world values to listener-relative
                    Vector3 listenerPos = listener.Position;
                    listener.GetOrientation(out Vector3 listenerForward, out Vector3 listenerUp);
                    Matrix4x4 listenerTransform = Matrix4x4.CreateWorld(listenerPos, listenerForward, listenerUp);
                    if (Matrix4x4.Invert(listenerTransform, out Matrix4x4 invListenerTransform))
                    {
                        Vector3 relativePosition = Vector3.Transform(worldPosition, invListenerTransform);
                        source.Velocity = delta > 0.0f ? (relativePosition - source.Position) / delta : Vector3.Zero;
                        source.Position = relativePosition;
                        source.Direction = Vector3.TransformNormal(worldForward, invListenerTransform);
                    }
                }
                else
                {
                    source.Velocity = delta > 0.0f ? (worldPosition - source.Position) / delta : Vector3.Zero;
                    source.Position = worldPosition;
                    source.Direction = worldForward;
                }

                //TODO: manage streaming buffers and handle state update when all sources are stopped
                //ESourceState state = source.SourceState;
                //int queuedBuffers = source.BuffersQueued;
                //int processedBuffers = source.BuffersProcessed;
                //var byteOffset = source.ByteOffset;
                //var sampleOffset = source.SampleOffset;
                //var secondsOffset = source.SecondsOffset;
                //Debug.Out($"State: {state}, Queued: {queuedBuffers}, Processed: {processedBuffers}, ByteOffset: {byteOffset}, SampleOffset: {sampleOffset}, SecondsOffset: {secondsOffset}");
            }
        }

        private void UpdateValidListeners(Vector3 worldPosition)
        {
            if (World is null || (DateTime.Now - _lastExistenceCheckTime) < ExistenceCheckInterval)
                return;

            //lock (ActiveListeners)
            //{
                _lastExistenceCheckTime = DateTime.Now;
                //There will usually only be one listener, but we support multiple for future-proofing
                //Check if listener is within range, add and remove sources as needed
                foreach (var listener in World.Listeners)
                {
                    if (RelativeToListener)
                        AddSourceToListener(listener);
                    else
                    {
                        float gain = listener.CalcGain(worldPosition, ReferenceDistance, MaxDistance, RolloffFactor);
                        (gain > float.Epsilon ? (Action<ListenerContext>)AddSourceToListener : RemoveSourceFromListener)(listener);
                    }
                }
            //}
        }

        private DateTime _lastExistenceCheckTime = DateTime.MinValue;

        private TimeSpan _existenceCheckInterval = TimeSpan.FromSeconds(1.0);
        public TimeSpan ExistenceCheckInterval 
        {
            get => _existenceCheckInterval;
            set => SetField(ref _existenceCheckInterval, value);
        }

        public void AddSourceToListener(ListenerContext listener)
            => ActiveListeners.GetOrAdd(listener, AddSource);

        private AudioSource AddSource(ListenerContext x)
        {
            var s = x.TakeSource();
            //s.SourceType = Type;
            s.RolloffFactor = RolloffFactor;
            s.ReferenceDistance = ReferenceDistance;
            s.MaxDistance = MaxDistance;
            s.RelativeToListener = RelativeToListener;
            s.Looping = Loop;
            s.Pitch = Pitch;
            s.MinGain = MinGain;
            s.MaxGain = MaxGain;
            s.Gain = Gain;
            s.ConeInnerAngle = ConeInnerAngle;
            s.ConeOuterAngle = ConeOuterAngle;
            s.ConeOuterGain = ConeOuterGain;
            StaticBufferChanged(x, s);
            StateChanged(s);
            return s;
        }

        private void RemoveSourceFromListener(ListenerContext listener)
        {
            if (ActiveListeners.TryRemove(listener, out var source))
                listener.ReleaseSource(source);
        }

        /// <summary>
        /// Gets the current frequency analysis levels from playing audio.
        /// Returns bass (20-250Hz), mids (250-2000Hz) and treble (2000-20000Hz) levels normalized between 0-1.
        /// Returns (0,0,0) if no audio is playing or no valid samples could be retrieved.
        /// </summary>
        public (float bass, float mids, float treble) GetFFTLevels(int sampleCount = 2048)
        {
            if (_staticBuffer is null)
                return (0f, 0f, 0f);

            // Try to get samples from any active source
            AudioSource source;
            //lock (ActiveListeners)
                source = ActiveListeners.FirstOrDefault().Value;

            if (source.SourceState != ESourceState.Playing || source.Buffer == null)
                return (0f, 0f, 0f);

            float[] samples = new float[sampleCount.CeilingToPowerOfTwo()];
            if (!_staticBuffer.GetSamples(samples, source.SampleOffset))
                return (0f, 0f, 0f);
            
            var bassRange = (250.0f, AudioBuffer.EMagAccumMethod.Average);
            var midsRange = (2000.0f, AudioBuffer.EMagAccumMethod.Average);
            var trebleRange = (20000.0f, AudioBuffer.EMagAccumMethod.Average);
            var (bass, mids, treble) = AudioBuffer.FastFourier(
                samples,
                _staticBuffer.Frequency,
                bassRange,
                midsRange,
                trebleRange
            );

            return (bass.Clamp(0f, 1f), mids.Clamp(0f, 1f), treble.Clamp(0f, 1f));
        }
    }
}
