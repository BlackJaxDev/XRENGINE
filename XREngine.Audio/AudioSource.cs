using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;
using System.Diagnostics;
using System.Numerics;
using XREngine.Core;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Audio.Steam;

namespace XREngine.Audio
{
    public sealed class AudioSource : IDisposable, IPoolable
    {
        private bool IsV2 => ParentListener.IsV2 && ParentListener.Transport is not null;

        internal AudioSource(ListenerContext parentListener)
        {
            ParentListener = parentListener;
            Api = parentListener.Api;
            CreateNativeSource();
        }

        public ListenerContext ParentListener { get; }
        public AL Api { get; }
        private AudioSourceHandle _transportHandle;
        private EffectsSourceHandle? _effectsHandle;

        public uint Handle => _transportHandle.Id;
        internal AudioSourceHandle TransportHandle => _transportHandle;
        internal EffectsSourceHandle? EffectsHandle => _effectsHandle;

        private void CreateNativeSource()
        {
            if (IsV2)
            {
                _transportHandle = ParentListener.Transport!.CreateSource();
                RegisterEffectsSource();
                return;
            }

            _transportHandle = new AudioSourceHandle(Api.GenSource());
            ParentListener.VerifyError();
        }

        private void DestroyNativeSource()
        {
            if (!_transportHandle.IsValid)
                return;

            if (IsV2)
            {
                UnregisterEffectsSource();
                ParentListener.Transport!.DestroySource(_transportHandle);
            }
            else
            {
                Api.SourceStop(Handle);
                Api.DeleteSource(Handle);
            }

            ParentListener.VerifyError();
            _transportHandle = AudioSourceHandle.Invalid;
        }

        private void RegisterEffectsSource()
        {
            UnregisterEffectsSource();

            var processor = ParentListener.EffectsProcessor;
            if (processor is null)
                return;
            if (processor is SteamAudioProcessor && _bypassSteamAudioSpatialization)
                return;

            EffectsSourceHandle effectsHandle = processor.AddSource(new AudioEffectsSourceSettings
            {
                Position = _position,
                Forward = GetEffectsForward(),
                InputChannels = _steamAudioNonSpatialStereo ? 2 : 1,
                SpatialBlend = _steamAudioNonSpatialStereo ? 0.0f : 1.0f,
            });

            _effectsHandle = effectsHandle.IsValid ? effectsHandle : null;
            SyncEffectsSourcePose();
        }

        private void UnregisterEffectsSource()
        {
            if (_effectsHandle is not { } effectsHandle || !effectsHandle.IsValid)
            {
                _effectsHandle = null;
                return;
            }

            ParentListener.EffectsProcessor?.RemoveSource(effectsHandle);
            _effectsHandle = null;
        }

        private Vector3 GetEffectsForward()
            => _direction.LengthSquared() > 0.0001f
                ? Vector3.Normalize(_direction)
                : -Vector3.UnitZ;

        private void SyncEffectsSourcePose()
        {
            if (_effectsHandle is not { } effectsHandle || !effectsHandle.IsValid)
                return;

            ParentListener.EffectsProcessor?.SetSourcePose(effectsHandle, _position, GetEffectsForward());
        }

        public void Dispose()
        {
            if (!_transportHandle.IsValid)
                return;

            DestroyNativeSource();

            GC.SuppressFinalize(this);
        }

        #region Buffers
        /// <summary>
        /// The number of buffers queued on this source.
        /// </summary>
        public int BuffersQueued
            => IsV2
                ? (_currentStreamingBuffers.Count > 0 ? _currentStreamingBuffers.Count : (_bufferHandle.IsValid ? 1 : 0))
                : GetBuffersQueued();
        /// <summary>
        /// The number of buffers in the queue that have been processed.
        /// </summary>
        public int BuffersProcessed
            => GetBuffersProcessed();
        /// <summary>
        /// The buffer that the source is playing from.
        /// </summary>
        public AudioBuffer? Buffer
        {
            get => ParentListener.GetBufferByHandle(IsV2 ? _bufferHandle.Id : GetBufferHandle());
            set
            {
                if (value is not null)
                    SetBufferHandle(value.Handle);
                else
                    SetBufferHandle(0);
            }
        }

        private readonly Queue<AudioBuffer> _currentStreamingBuffers = [];
        public Queue<AudioBuffer> CurrentStreamingBuffers => _currentStreamingBuffers;

        public XREvent<AudioBuffer>? BufferQueued;
        public XREvent<AudioBuffer>? BufferProcessed;

        /// <summary>
        /// When <c>true</c> (default), <see cref="QueueBuffers"/> automatically
        /// starts playback after enqueuing.  Set to <c>false</c> for streaming
        /// scenarios where the caller controls playback timing (e.g. pre-buffering).
        /// </summary>
        public bool AutoPlayOnQueue { get; set; } = true;

        public void SetBufferData(AudioBuffer buffer, byte[] data, int frequency, bool stereo)
        {
            if (!TryProcessBufferData(data, frequency, stereo, out float[] processedData, out bool processedStereo))
            {
                buffer.SetData(data, frequency, stereo);
                return;
            }

            ArgumentNullException.ThrowIfNull(processedData);
            buffer.SetData(processedData, frequency, processedStereo);
        }

        public void SetBufferData(AudioBuffer buffer, short[] data, int frequency, bool stereo)
        {
            if (!TryProcessBufferData(data, frequency, stereo, out float[] processedData, out bool processedStereo))
            {
                buffer.SetData(data, frequency, stereo);
                return;
            }

            ArgumentNullException.ThrowIfNull(processedData);
            buffer.SetData(processedData, frequency, processedStereo);
        }

        public void SetBufferData(AudioBuffer buffer, float[] data, int frequency, bool stereo)
        {
            if (!TryProcessBufferData(data, frequency, stereo, out float[] processedData, out bool processedStereo))
            {
                buffer.SetData(data, frequency, stereo);
                return;
            }

            ArgumentNullException.ThrowIfNull(processedData);
            buffer.SetData(processedData, frequency, processedStereo);
        }

        public void SetBufferData(AudioBuffer buffer, AudioData data)
        {
            if (!TryProcessBufferData(data, out float[] processedData, out bool processedStereo))
            {
                buffer.SetData(data);
                return;
            }

            ArgumentNullException.ThrowIfNull(processedData);
            buffer.SetData(processedData, data.Frequency, processedStereo);
        }

        private bool TryProcessBufferData(byte[] data, int frequency, bool stereo, out float[] processedData, out bool processedStereo)
        {
            if (!TryGetSteamAudioProcessor(out var processor, out var effectsHandle))
            {
                processedData = [];
                processedStereo = stereo;
                return false;
            }

            int inputChannels = _steamAudioNonSpatialStereo && stereo ? 2 : 1;
            float[] input = inputChannels == 2 ? ConvertBytesToStereoFloat(data) : ConvertBytesToMonoFloat(data, stereo);
            processedData = ProcessSteamAudioBuffer(processor, effectsHandle, input, frequency, inputChannels);
            processedStereo = true;
            return true;
        }

        private bool TryProcessBufferData(short[] data, int frequency, bool stereo, out float[] processedData, out bool processedStereo)
        {
            if (!TryGetSteamAudioProcessor(out var processor, out var effectsHandle))
            {
                processedData = [];
                processedStereo = stereo;
                return false;
            }

            int inputChannels = _steamAudioNonSpatialStereo && stereo ? 2 : 1;
            float[] input = inputChannels == 2 ? ConvertShortsToStereoFloat(data) : ConvertShortsToMonoFloat(data, stereo);
            processedData = ProcessSteamAudioBuffer(processor, effectsHandle, input, frequency, inputChannels);
            processedStereo = true;
            return true;
        }

        private bool TryProcessBufferData(float[] data, int frequency, bool stereo, out float[] processedData, out bool processedStereo)
        {
            if (!TryGetSteamAudioProcessor(out var processor, out var effectsHandle))
            {
                processedData = [];
                processedStereo = stereo;
                return false;
            }

            int inputChannels = _steamAudioNonSpatialStereo && stereo ? 2 : 1;
            float[] input = inputChannels == 2 ? [.. data] : stereo ? ConvertStereoToMono(data) : [.. data];
            processedData = ProcessSteamAudioBuffer(processor, effectsHandle, input, frequency, inputChannels);
            processedStereo = true;
            return true;
        }

        private bool TryProcessBufferData(AudioData data, out float[] processedData, out bool processedStereo)
        {
            if (!TryGetSteamAudioProcessor(out var processor, out var effectsHandle) || data.Data is null)
            {
                processedData = [];
                processedStereo = data.Stereo;
                return false;
            }

            int inputChannels = _steamAudioNonSpatialStereo && data.Stereo ? 2 : 1;
            float[] input = (data.Type, inputChannels) switch
            {
                (AudioData.EPCMType.Byte, 2) => ConvertBytesToStereoFloat(data.GetByteData()),
                (AudioData.EPCMType.Byte, _) => ConvertBytesToMonoFloat(data.GetByteData(), data.Stereo),
                (AudioData.EPCMType.Short, 2) => ConvertShortsToStereoFloat(data.GetShortData()),
                (AudioData.EPCMType.Short, _) => ConvertShortsToMonoFloat(data.GetShortData(), data.Stereo),
                (AudioData.EPCMType.Float, 2) => [.. data.GetFloatData()],
                (AudioData.EPCMType.Float, _) => data.Stereo ? ConvertStereoToMono(data.GetFloatData()) : data.GetFloatData(),
                (_, 2) => [.. data.GetFloatData()],
                _ => data.Stereo ? ConvertStereoToMono(data.GetFloatData()) : data.GetFloatData(),
            };

            processedData = ProcessSteamAudioBuffer(processor, effectsHandle, input, data.Frequency, inputChannels);
            processedStereo = true;
            return true;
        }

        private bool TryGetSteamAudioProcessor(out SteamAudioProcessor processor, out EffectsSourceHandle effectsHandle)
        {
            processor = null!;
            effectsHandle = EffectsSourceHandle.Invalid;

            if (_bypassSteamAudioSpatialization)
                return false;

            if (!IsV2 || ParentListener.EffectsProcessor is not SteamAudioProcessor steamProcessor)
                return false;

            if (_effectsHandle is not { } handle || !handle.IsValid)
                return false;

            processor = steamProcessor;
            effectsHandle = handle;
            return true;
        }

        private static float[] ProcessSteamAudioBuffer(SteamAudioProcessor processor, EffectsSourceHandle effectsHandle, float[] input, int frequency, int inputChannels)
        {
            int frameCount = input.Length / Math.Max(1, inputChannels);
            float[] processed = new float[frameCount * 2];
            processor.ProcessBuffer(effectsHandle, input, processed, inputChannels, frequency);
            return processed;
        }

        private static float[] ConvertBytesToStereoFloat(byte[] data)
        {
            float[] stereo = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
                stereo[i] = data[i] / 128f - 1f;
            return stereo;
        }

        private static float[] ConvertBytesToMonoFloat(byte[] data, bool stereo)
        {
            if (!stereo)
            {
                float[] mono = new float[data.Length];
                for (int i = 0; i < data.Length; i++)
                    mono[i] = data[i] / 128f - 1f;
                return mono;
            }

            int frameCount = data.Length / 2;
            float[] mixed = new float[frameCount];
            for (int frame = 0, sample = 0; frame < frameCount; frame++, sample += 2)
            {
                float left = data[sample] / 128f - 1f;
                float right = data[sample + 1] / 128f - 1f;
                mixed[frame] = (left + right) * 0.5f;
            }

            return mixed;
        }

        private static float[] ConvertShortsToMonoFloat(short[] data, bool stereo)
        {
            if (!stereo)
            {
                float[] mono = new float[data.Length];
                for (int i = 0; i < data.Length; i++)
                    mono[i] = data[i] / 32768f;
                return mono;
            }

            int frameCount = data.Length / 2;
            float[] mixed = new float[frameCount];
            for (int frame = 0, sample = 0; frame < frameCount; frame++, sample += 2)
            {
                float left = data[sample] / 32768f;
                float right = data[sample + 1] / 32768f;
                mixed[frame] = (left + right) * 0.5f;
            }

            return mixed;
        }

        private static float[] ConvertShortsToStereoFloat(short[] data)
        {
            float[] stereo = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
                stereo[i] = data[i] / 32768f;
            return stereo;
        }

        private static float[] ConvertStereoToMono(float[] data)
        {
            int frameCount = data.Length / 2;
            float[] mixed = new float[frameCount];
            for (int frame = 0, sample = 0; frame < frameCount; frame++, sample += 2)
                mixed[frame] = (data[sample] + data[sample + 1]) * 0.5f;

            return mixed;
        }

        public unsafe bool QueueBuffers(int maxbuffers, params AudioBuffer[] buffers)
        {
            if (!IsV2)
                ParentListener.MakeCurrent();
            int buffersProcessed = BuffersProcessed;
            if (buffersProcessed > 0)
                UnqueueConsumedBuffers(buffersProcessed);
            
            if (BuffersQueued >= maxbuffers)
            {
                // Return the passed-in buffers to the pool so they aren't leaked.
                foreach (var leaked in buffers)
                    ParentListener.ReleaseBuffer(leaked);
                AudioDiagnostics.RecordBufferOverflow(Handle, buffers.Length);
                return false;
            }

            uint* handles = stackalloc uint[buffers.Length];
            for (int i = 0; i < buffers.Length; i++)
            {
                var buf = buffers[i];
                _currentStreamingBuffers.Enqueue(buf);
                BufferQueued?.Invoke(buf);
                handles[i] = buf.Handle;
            }
            if (IsV2)
            {
                Span<AudioBufferHandle> queueHandles = stackalloc AudioBufferHandle[buffers.Length];
                for (int i = 0; i < buffers.Length; i++)
                    queueHandles[i] = new AudioBufferHandle(handles[i]);
                ParentListener.Transport!.QueueBuffers(_transportHandle, queueHandles);
                _sourceType = ESourceType.Streaming;
            }
            else
            {
                Api.SourceQueueBuffers(Handle, buffers.Length, handles);
                ParentListener.VerifyError();
            }
            AudioDiagnostics.RecordBuffersQueued(Handle, buffers.Length, BuffersQueued);

            if (AutoPlayOnQueue && !IsPlaying)
            {
                Play();
            }
            return true;
        }
        //public unsafe void UnqueueBuffers(params AudioBuffer[] buffers)
        //{
        //    uint[] handles = new uint[buffers.Length];
        //    for (int i = 0; i < buffers.Length; i++)
        //        handles[i] = buffers[i].Handle;
        //    fixed (uint* pBuffers = handles)
        //        Api.SourceUnqueueBuffers(Handle, buffers.Length, pBuffers);
        //}
        public unsafe void UnqueueConsumedBuffers(int requestedCount = 0)
        {
            if (!IsV2)
                ParentListener.MakeCurrent();
            bool looping = GetLooping();
            if (looping)
                Debug.WriteLine("Warning: UnqueueConsumedBuffers called on a looping source.");

            int processedNow = BuffersProcessed;
            int requested = requestedCount <= 0 ? processedNow : requestedCount;
            int count = Math.Min(_currentStreamingBuffers.Count, Math.Min(requested, processedNow));
            if (count == 0)
            {
                if (requestedCount > 0)
                    AudioDiagnostics.RecordBufferUnderflow(Handle, _currentStreamingBuffers.Count);
                return;
            }

            uint* handles = stackalloc uint[count];
            if (IsV2)
            {
                Span<AudioBufferHandle> unqueued = stackalloc AudioBufferHandle[count];
                int returned = ParentListener.Transport!.UnqueueProcessedBuffers(_transportHandle, unqueued);
                count = Math.Min(count, returned);
                for (int i = 0; i < count; i++)
                    handles[i] = unqueued[i].Id;
            }
            else
            {
                Api.SourceUnqueueBuffers(Handle, count, handles);
                ParentListener.VerifyError();
            }

            // Keep our managed queue in sync with OpenAL's processed queue.
            for (int i = 0; i < count; i++)
            {
                uint handle = handles[i];
                if (handle == 0)
                    continue;

                AudioBuffer? buf = RemoveTrackedStreamingBuffer(handle);
                if (buf is null)
                {
                    Debug.WriteLine($"Warning: Streaming buffer handle {handle} was unqueued by OpenAL but not tracked locally.");
                    continue;
                }

                BufferProcessed?.Invoke(buf);
                ParentListener.ReleaseBuffer(buf);
            }
            //Trace.WriteLineIf(handles.Length > 0, $"Unqueued {handles.Length} buffers.");
            AudioDiagnostics.RecordBuffersUnqueued(Handle, count, _currentStreamingBuffers.Count);
        }

        private AudioBuffer? RemoveTrackedStreamingBuffer(uint handle)
        {
            if (_currentStreamingBuffers.Count == 0)
                return null;

            AudioBuffer? found = null;
            int remaining = _currentStreamingBuffers.Count;
            for (int i = 0; i < remaining; i++)
            {
                AudioBuffer item = _currentStreamingBuffers.Dequeue();
                if (found is null && item.Handle == handle)
                {
                    found = item;
                    continue;
                }

                _currentStreamingBuffers.Enqueue(item);
            }

            return found;
        }

        private void ReleaseAllTrackedStreamingBuffers()
        {
            while (_currentStreamingBuffers.Count > 0)
            {
                AudioBuffer buffer = _currentStreamingBuffers.Dequeue();
                if (buffer is not null)
                    ParentListener.ReleaseBuffer(buffer);
            }
        }
        #endregion

        #region Offset
        /// <summary>
        /// The playback position, expressed in seconds.
        /// </summary>
        public float SecondsOffset
        {
            get => GetSecOffset();
            set => SetSecOffset(value);
        }
        /// <summary>
        /// The offset in bytes of the source.
        /// </summary>
        public int ByteOffset
        {
            get => GetByteOffset();
            set => SetByteOffset(value);
        }
        /// <summary>
        /// The offset in samples of the source.
        /// </summary>
        public int SampleOffset
        {
            get => GetSampleOffset();
            set => SetSampleOffset(value);
        }
        #endregion

        public enum ESourceState
        {
            Initial,
            Playing,
            Paused,
            Stopped,
        }

        public enum ESourceType
        {
            Static,
            Streaming,
            Undetermined,
        }

        private static ESourceState ConvSourceState(int rawState)
            => rawState switch
            {
                // OpenAL enum values
                (int)Silk.NET.OpenAL.SourceState.Initial => ESourceState.Initial,
                (int)Silk.NET.OpenAL.SourceState.Playing => ESourceState.Playing,
                (int)Silk.NET.OpenAL.SourceState.Paused => ESourceState.Paused,
                (int)Silk.NET.OpenAL.SourceState.Stopped => ESourceState.Stopped,

                // NAudio PlaybackState values (Stopped=0, Playing=1, Paused=2)
                0 => ESourceState.Stopped,
                1 => ESourceState.Playing,
                2 => ESourceState.Paused,

                // Common fallback for unknown/invalid values
                _ => ESourceState.Stopped,
            };

        public static SourceState ConvSourceState(ESourceState state)
            => state switch
            {
                ESourceState.Initial => Silk.NET.OpenAL.SourceState.Initial,
                ESourceState.Playing => Silk.NET.OpenAL.SourceState.Playing,
                ESourceState.Paused => Silk.NET.OpenAL.SourceState.Paused,
                ESourceState.Stopped => Silk.NET.OpenAL.SourceState.Stopped,
                _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
            };

        public static ESourceType ConvSourceType(SourceType type)
            => type switch
            {
                Silk.NET.OpenAL.SourceType.Static => ESourceType.Static,
                Silk.NET.OpenAL.SourceType.Streaming => ESourceType.Streaming,
                Silk.NET.OpenAL.SourceType.Undetermined => ESourceType.Undetermined,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
            };

        public static SourceType ConvSourceType(ESourceType type)
            => type switch
            {
                ESourceType.Static => Silk.NET.OpenAL.SourceType.Static,
                ESourceType.Streaming => Silk.NET.OpenAL.SourceType.Streaming,
                ESourceType.Undetermined => Silk.NET.OpenAL.SourceType.Undetermined,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
            };

        #region State
        public bool IsPlaying
            => SourceState == ESourceState.Playing;
        public bool IsStopped
            => SourceState == ESourceState.Stopped;
        public bool IsPaused
            => SourceState == ESourceState.Paused;
        public bool IsInitial
            => SourceState == ESourceState.Initial;
        /// <summary>
        /// The state of the source (Stopped, Playing, etc).
        /// </summary>
        public ESourceState SourceState
        {
            get
            {
                if (IsV2)
                {
                    // Poll transport so we detect auto-stop (e.g. all buffers consumed).
                    GetSourceState();
                    return _sourceState;
                }
                return ConvSourceState(GetSourceState());
            }
        }
        /// <summary>
        /// Plays the source.
        /// </summary>
        public void Play()
        {
            var prev = SourceState;
            if (IsV2)
                ParentListener.Transport!.Play(_transportHandle);
            else
            {
                Api.SourcePlay(Handle);
                ParentListener.VerifyError();
            }
            _sourceState = ESourceState.Playing;
            AudioDiagnostics.RecordSourceStateChange(Handle, prev.ToString(), nameof(ESourceState.Playing));
        }
        /// <summary>
        /// Stops the source from playing.
        /// </summary>
        public void Stop()
        {
            var prev = SourceState;
            if (IsV2)
                ParentListener.Transport!.Stop(_transportHandle);
            else
            {
                Api.SourceStop(Handle);
                ParentListener.VerifyError();
            }
            _sourceState = ESourceState.Stopped;
            AudioDiagnostics.RecordSourceStateChange(Handle, prev.ToString(), nameof(ESourceState.Stopped));
        }
        /// <summary>
        /// Pauses the source.
        /// </summary>
        public void Pause()
        {
            var prev = SourceState;
            if (IsV2)
                ParentListener.Transport!.Pause(_transportHandle);
            else
            {
                Api.SourcePause(Handle);
                ParentListener.VerifyError();
            }
            _sourceState = ESourceState.Paused;
            AudioDiagnostics.RecordSourceStateChange(Handle, prev.ToString(), nameof(ESourceState.Paused));
        }
        /// <summary>
        /// Sets the source to play from the beginning (initial state).
        /// </summary>
        public void Rewind()
        {
            var prev = SourceState;
            if (!IsV2)
            {
                Api.SourceRewind(Handle);
                ParentListener.VerifyError();
            }
            else
            {
                ParentListener.Transport!.Rewind(_transportHandle);
                _secondsOffset = 0.0f;
                _byteOffset = 0;
                _sampleOffset = 0;
            }
            _sourceState = ESourceState.Initial;
            AudioDiagnostics.RecordSourceStateChange(Handle, prev.ToString(), nameof(ESourceState.Initial));
        }
        #endregion

        #region Settings

        /// <summary>
        /// The type of the source, either Static, Streaming, or undetermined.
        /// Source is set to Streaming if one or more buffers have been attached using SourceQueueBuffers.
        /// </summary>
        public ESourceType SourceType
        {
            get => IsV2 ? _sourceType : ConvSourceType((SourceType)GetSourceType());
            //set => SetSourceType((int)ConvSourceType(value));
        }
        /// <summary>
        /// If true, the source's position is relative to the listener.
        /// If false, the source's position is in world space.
        /// </summary>
        public bool RelativeToListener
        {
            get => GetSourceRelative();
            set => SetSourceRelative(value);
        }
        /// <summary>
        /// If true, the source will loop.
        /// </summary>
        public bool Looping
        {
            get => GetLooping();
            set => SetLooping(value);
        }
        /// <summary>
        /// If true, Steam Audio is bypassed for this source so stereo media stays normal stereo.
        /// </summary>
        public bool BypassSteamAudioSpatialization
        {
            get => _bypassSteamAudioSpatialization;
            set => SetBypassSteamAudioSpatialization(value);
        }
        /// <summary>
        /// If true, Steam Audio preserves the source's stereo image instead of spatializing it.
        /// </summary>
        public bool SteamAudioNonSpatialStereo
        {
            get => _steamAudioNonSpatialStereo;
            set => SetSteamAudioNonSpatialStereo(value);
        }
        /// <summary>
        /// How far the source is from the listener.
        /// At 0.0f, no distance attenuation occurs.
        /// Default: 1.0f.
        /// Range: [0.0f - float.PositiveInfinity] 
        /// </summary>
        public float ReferenceDistance
        {
            get => GetReferenceDistance();
            set => SetReferenceDistance(value);
        }
        /// <summary>
        /// The distance above which sources are not attenuated using the inverse clamped distance model.
        /// Default: float.PositiveInfinity
        /// Range: [0.0f - float.PositiveInfinity]
        /// </summary>
        public float MaxDistance
        {
            get => GetMaxDistance();
            set => SetMaxDistance(value);
        }
        /// <summary>
        /// The rolloff factor of the source.
        /// Rolloff factor is the rate at which the source's volume decreases as it moves further from the listener.
        /// Range: [0.0f - float.PositiveInfinity]
        /// </summary>
        public float RolloffFactor
        {
            get => GetRolloffFactor();
            set => SetRolloffFactor(value);
        }
        /// <summary>
        /// The pitch of the source.
        /// Default: 1.0f
        /// Range: [0.5f - 2.0f]
        /// </summary>
        public float Pitch
        {
            get => GetPitch();
            set => SetPitch(value);
        }
        /// <summary>
        /// The minimum gain of the source.
        /// Range: [0.0f - 1.0f] (Logarithmic)
        /// </summary>
        public float MinGain
        {
            get => GetMinGain();
            set => SetMinGain(value);
        }
        /// <summary>
        /// The maximum gain of the source.
        /// Range: [0.0f - 1.0f] (Logarithmic)
        /// </summary>
        public float MaxGain
        {
            get => GetMaxGain();
            set => SetMaxGain(value);
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
            get => GetGain();
            set => SetGain(value);
        }
        /// <summary>
        /// Directional source, inner cone angle, in degrees.
        /// Default: 360
        /// Range: [0-360]
        /// </summary>
        public float ConeInnerAngle
        {
            get => GetConeInnerAngle();
            set => SetConeInnerAngle(value);
        }
        /// <summary>
        /// Directional source, outer cone angle, in degrees.
        /// Default: 360
        /// Range: [0-360]
        /// </summary>
        public float ConeOuterAngle
        {
            get => GetConeOuterAngle();
            set => SetConeOuterAngle(value);
        }
        /// <summary>
        /// Directional source, outer cone gain.
        /// Default: 0.0f
        /// Range: [0.0f - 1.0] (Logarithmic)
        /// </summary>
        public float ConeOuterGain
        {
            get => GetConeOuterGain();
            set => SetConeOuterGain(value);
        }
        #endregion

        #region Location
        /// <summary>
        /// The position of the source in world space.
        /// </summary>
        public Vector3 Position
        {
            get => GetPosition();
            set => SetPosition(value);
        }
        /// <summary>
        /// The velocity of the source.
        /// </summary>
        public Vector3 Velocity
        {
            get => GetVelocity();
            set => SetVelocity(value);
        }
        /// <summary>
        /// The direction the source is facing.
        /// </summary>
        public Vector3 Direction
        {
            get => GetDirection();
            set => SetDirection(value);
        }
        #endregion

        #region Get / Set Methods
        private ESourceState _sourceState = ESourceState.Initial;
        private ESourceType _sourceType = ESourceType.Undetermined;
        private AudioBufferHandle _bufferHandle;
        private bool _sourceRelative;
        private bool _looping;
        private bool _bypassSteamAudioSpatialization;
        private bool _steamAudioNonSpatialStereo;
        private int _byteOffset;
        private int _sampleOffset;
        private float _secondsOffset;
        private Vector3 _position;
        private Vector3 _velocity;
        private Vector3 _direction;
        private float _referenceDistance = 1.0f;
        private float _maxDistance = float.PositiveInfinity;
        private float _rolloffFactor = 1.0f;
        private float _pitch = 1.0f;
        private float _minGain;
        private float _maxGain = 1.0f;
        private float _gain = 1.0f;
        private float _coneInnerAngle = 360.0f;
        private float _coneOuterAngle = 360.0f;
        private float _coneOuterGain;

        private bool GetSourceRelative()
        {
            if (IsV2)
                return _sourceRelative;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceBoolean.SourceRelative, out bool value);
            ParentListener.VerifyError();
            return value;
        }
        private bool GetLooping()
        {
            if (IsV2)
                return _looping;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceBoolean.Looping, out bool value);
            ParentListener.VerifyError();
            return value;
        }
        private void SetSourceRelative(bool relative)
        {
            _sourceRelative = relative;
            if (IsV2)
                return;
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceBoolean.SourceRelative, relative);
            ParentListener.VerifyError();
        }
        private void SetLooping(bool loop)
        {
            _looping = loop;
            if (IsV2)
            {
                ParentListener.Transport!.SetSourceLooping(_transportHandle, loop);
                return;
            }
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceBoolean.Looping, loop);
            ParentListener.VerifyError();
        }
        private void SetBypassSteamAudioSpatialization(bool bypass)
        {
            if (_bypassSteamAudioSpatialization == bypass)
                return;

            _bypassSteamAudioSpatialization = bypass;

            if (!IsV2 || ParentListener.EffectsProcessor is not SteamAudioProcessor)
                return;

            if (bypass)
                UnregisterEffectsSource();
            else if (_transportHandle.IsValid)
                RegisterEffectsSource();
        }

        private void SetSteamAudioNonSpatialStereo(bool enabled)
        {
            if (_steamAudioNonSpatialStereo == enabled)
                return;

            _steamAudioNonSpatialStereo = enabled;

            if (!IsV2 || ParentListener.EffectsProcessor is not SteamAudioProcessor || _bypassSteamAudioSpatialization)
                return;

            if (_transportHandle.IsValid)
            {
                UnregisterEffectsSource();
                RegisterEffectsSource();
            }
        }

        private int GetByteOffset()
        {
            if (IsV2)
                return _byteOffset;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, GetSourceInteger.ByteOffset, out int value);
            ParentListener.VerifyError();
            return value;
        }
        private int GetSampleOffset()
        {
            if (IsV2)
                return ParentListener.Transport!.GetSampleOffset(_transportHandle);
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, GetSourceInteger.SampleOffset, out int value);
            ParentListener.VerifyError();
            return value;
        }
        private unsafe uint GetBufferHandle()
        {
            if (IsV2)
                return _bufferHandle.Id;
            ParentListener.MakeCurrent();
            uint buffer;
            Api.GetSourceProperty(Handle, GetSourceInteger.Buffer, (int*)&buffer);
            ParentListener.VerifyError();
            return buffer;
        }
        private int GetSourceType()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, GetSourceInteger.SourceType, out int value);
            ParentListener.VerifyError();
            return value;
        }
        private int GetSourceState()
        {
            if (IsV2)
            {
                // Query the transport for the real source state so we detect when
                // OpenAL (or NAudio) stops the source after all buffers are consumed.
                bool playing = ParentListener.Transport!.IsSourcePlaying(_transportHandle);
                if (playing)
                    _sourceState = ESourceState.Playing;
                else if (_sourceState == ESourceState.Playing)
                    _sourceState = ESourceState.Stopped; // source ran out of buffers
                return (int)_sourceState;
            }
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, GetSourceInteger.SourceState, out int value);
            ParentListener.VerifyError();
            return value;
        }
        private unsafe int GetBuffersQueued()
        {
            if (IsV2)
                return _currentStreamingBuffers.Count;
            ParentListener.MakeCurrent();
            int value;
            Api.GetSourceProperty(Handle, GetSourceInteger.BuffersQueued, &value);
            ParentListener.VerifyError();
            return value;
        }
        private unsafe int GetBuffersProcessed()
        {
            if (IsV2)
                return ParentListener.Transport!.GetBuffersProcessed(_transportHandle);
            ParentListener.MakeCurrent();
            int value;
            Api.GetSourceProperty(Handle, GetSourceInteger.BuffersProcessed, &value);
            ParentListener.VerifyError();
            return value;
        }

        private void SetByteOffset(int offset)
        {
            _byteOffset = offset;
            if (IsV2)
                return;
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceInteger.ByteOffset, offset);
            ParentListener.VerifyError();
        }
        private void SetSampleOffset(int offset)
        {
            _sampleOffset = offset;
            if (IsV2)
                return;
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceInteger.SampleOffset, offset);
            ParentListener.VerifyError();
        }
        private void SetBufferHandle(uint buffer)
        {
            _bufferHandle = new AudioBufferHandle(buffer);
            if (IsV2)
            {
                ParentListener.Transport!.SetSourceBuffer(_transportHandle, _bufferHandle);
                _sourceType = buffer == 0 ? ESourceType.Undetermined : ESourceType.Static;
                return;
            }
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceInteger.Buffer, buffer);
            ParentListener.VerifyError();
        }

        //SourceType is read-only
        //private void SetSourceType(int type)
        //{
        //    Api.SetSourceProperty(Handle, SourceInteger.SourceType, type);
        //    ParentListener.VerifyError();
        //}

        private Vector3 GetPosition()
        {
            if (IsV2)
                return _position;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceVector3.Position, out Vector3 value);
            ParentListener.VerifyError();
            return value;
        }
        private Vector3 GetVelocity()
        {
            if (IsV2)
                return _velocity;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceVector3.Velocity, out Vector3 value);
            ParentListener.VerifyError();
            return value;
        }
        private Vector3 GetDirection()
        {
            if (IsV2)
                return _direction;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceVector3.Direction, out Vector3 value);
            ParentListener.VerifyError();
            return value;
        }

        private void SetPosition(Vector3 position)
        {
            _position = position;
            if (IsV2)
            {
                ParentListener.Transport!.SetSourcePosition(_transportHandle, position);
                SyncEffectsSourcePose();
                return;
            }
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceVector3.Position, position);
            ParentListener.VerifyError();
        }
        private void SetVelocity(Vector3 velocity)
        {
            _velocity = velocity;
            if (IsV2)
            {
                ParentListener.Transport!.SetSourceVelocity(_transportHandle, velocity);
                return;
            }
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceVector3.Velocity, velocity);
            ParentListener.VerifyError();
        }
        private void SetDirection(Vector3 direction)
        {
            _direction = direction;
            if (IsV2)
            {
                SyncEffectsSourcePose();
                return;
            }
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceVector3.Direction, direction);
            ParentListener.VerifyError();
        }

        private float GetReferenceDistance()
        {
            if (IsV2)
                return _referenceDistance;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.ReferenceDistance, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetMaxDistance()
        {
            if (IsV2)
                return _maxDistance;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.MaxDistance, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetRolloffFactor()
        {
            if (IsV2)
                return _rolloffFactor;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.RolloffFactor, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetPitch()
        {
            if (IsV2)
                return _pitch;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.Pitch, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetMinGain()
        {
            if (IsV2)
                return _minGain;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.MinGain, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetMaxGain()
        {
            if (IsV2)
                return _maxGain;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.MaxGain, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetGain()
        {
            if (IsV2)
                return _gain;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.Gain, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetConeInnerAngle()
        {
            if (IsV2)
                return _coneInnerAngle;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.ConeInnerAngle, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetConeOuterAngle()
        {
            if (IsV2)
                return _coneOuterAngle;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.ConeOuterAngle, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetConeOuterGain()
        {
            if (IsV2)
                return _coneOuterGain;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.ConeOuterGain, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetSecOffset()
        {
            if (IsV2)
                return _secondsOffset;
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.SecOffset, out float value);
            ParentListener.VerifyError();
            return value;
        }

        private void SetReferenceDistance(float distance)
        {
            _referenceDistance = distance;
            if (IsV2)
                return;
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.ReferenceDistance, distance);
            ParentListener.VerifyError();
        }
        private void SetMaxDistance(float distance)
        {
            _maxDistance = distance;
            if (IsV2)
                return;
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.MaxDistance, distance);
            ParentListener.VerifyError();
        }
        private void SetRolloffFactor(float factor)
        {
            _rolloffFactor = factor;
            if (IsV2)
                return;
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.RolloffFactor, factor);
            ParentListener.VerifyError();
        }
        private void SetPitch(float pitch)
        {
            _pitch = pitch;
            if (IsV2)
            {
                ParentListener.Transport!.SetSourcePitch(_transportHandle, pitch);
                return;
            }
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.Pitch, pitch);
            ParentListener.VerifyError();
        }
        private void SetMinGain(float gain)
        {
            _minGain = gain;
            if (IsV2)
                return;
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.MinGain, gain);
            ParentListener.VerifyError();
        }
        private void SetMaxGain(float gain)
        {
            _maxGain = gain;
            if (IsV2)
                return;
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.MaxGain, gain);
            ParentListener.VerifyError();
        }
        private void SetGain(float gain)
        {
            _gain = gain;
            if (IsV2)
            {
                ParentListener.Transport!.SetSourceGain(_transportHandle, gain);
                return;
            }
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.Gain, gain);
            ParentListener.VerifyError();
        }
        private void SetConeInnerAngle(float angle)
        {
            _coneInnerAngle = angle;
            if (IsV2)
                return;
            Api.SetSourceProperty(Handle, SourceFloat.ConeInnerAngle, angle);
            ParentListener.VerifyError();
        }
        private void SetConeOuterAngle(float angle)
        {
            _coneOuterAngle = angle;
            if (IsV2)
                return;
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.ConeOuterAngle, angle);
            ParentListener.VerifyError();
        }
        private void SetConeOuterGain(float gain)
        {
            _coneOuterGain = gain;
            if (IsV2)
                return;
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.ConeOuterGain, gain);
            ParentListener.VerifyError();
        }
        private void SetSecOffset(float offset)
        {
            _secondsOffset = offset;
            if (IsV2)
                return;
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.SecOffset, offset);
            ParentListener.VerifyError();
        }

        #endregion

        #region Effect Settings
        public int DirectFilter
        {
            get => GetDirectFilter();
            set => SetDirectFilter(value);
        }
        public float AirAbsorptionFactor
        {
            get => GetAirAbsorptionFactor();
            set => SetAirAbsorptionFactor(value);
        }
        public float RoomRolloffFactor
        {
            get => GetRoomRolloffFactor();
            set => SetRoomRolloffFactor(value);
        }
        public float ConeOuterGainHighFreq
        {
            get => GetConeOuterGainHF();
            set => SetConeOuterGainHF(value);
        }
        public bool DirectFilterGainHighFreqAuto
        {
            get => GetSourceProperty(EFXSourceBoolean.DirectFilterGainHighFrequencyAuto, out bool value) && value;
            set => SetSourceProperty(EFXSourceBoolean.DirectFilterGainHighFrequencyAuto, value);
        }
        public bool AuxiliarySendFilterGainAuto
        {
            get => GetSourceProperty(EFXSourceBoolean.AuxiliarySendFilterGainAuto, out bool value) && value;
            set => SetSourceProperty(EFXSourceBoolean.AuxiliarySendFilterGainAuto, value);
        }
        public bool AuxiliarySendFilterGainHighFrequencyAuto
        {
            get => GetSourceProperty(EFXSourceBoolean.AuxiliarySendFilterGainHighFrequencyAuto, out bool value) && value;
            set => SetSourceProperty(EFXSourceBoolean.AuxiliarySendFilterGainHighFrequencyAuto, value);
        }
        public struct AuxSendFilter
        {
            public int AuxEffectSlotID;
            public int AuxSendNumber;
            public int FilterID;
        }
        public AuxSendFilter AuxiliarySendFilter
        {
            get => GetAuxiliarySendFilter();
            set => SetAuxiliarySendFilter(value.AuxEffectSlotID, value.AuxSendNumber, value.FilterID);
        }
        #endregion

        #region Effects Get / Set Methods
        public AuxSendFilter GetAuxiliarySendFilter()
        {
            GetSourceProperty(EFXSourceInteger3.AuxiliarySendFilter, out int slotID, out int sendNumber, out int filterID);
            return new AuxSendFilter { AuxEffectSlotID = slotID, AuxSendNumber = sendNumber, FilterID = filterID };
        }
        public void SetAuxiliarySendFilter(int slotID, int sendNumber, int filterID)
        {
            SetSourceProperty(EFXSourceInteger3.AuxiliarySendFilter, slotID, sendNumber, filterID);
        }
        private void SetDirectFilter(int value)
        {
            SetSourceProperty(EFXSourceInteger.DirectFilter, value);
        }
        private int GetDirectFilter()
        {
            GetSourceProperty(EFXSourceInteger.DirectFilter, out int value);
            return value;
        }
        private void SetAirAbsorptionFactor(float value)
        {
            SetSourceProperty(EFXSourceFloat.AirAbsorptionFactor, value);
        }
        private float GetAirAbsorptionFactor()
        {
            GetSourceProperty(EFXSourceFloat.AirAbsorptionFactor, out float value);
            return value;
        }
        private void SetRoomRolloffFactor(float value)
        {
            SetSourceProperty(EFXSourceFloat.RoomRolloffFactor, value);
        }
        private float GetRoomRolloffFactor()
        {
            GetSourceProperty(EFXSourceFloat.RoomRolloffFactor, out float value);
            return value;
        }
        private void SetConeOuterGainHF(float value)
        {
            SetSourceProperty(EFXSourceFloat.ConeOuterGainHighFrequency, value);
        }
        private float GetConeOuterGainHF()
        {
            GetSourceProperty(EFXSourceFloat.ConeOuterGainHighFrequency, out float value);
            return value;
        }
        public bool GetSourceProperty(EFXSourceInteger param, out int value)
        {
            var eff = ParentListener.Effects?.Api;
            if (eff is null)
            {
                value = 0;
                return false;
            }

            eff.GetSourceProperty(Handle, param, out value);
            ParentListener.VerifyError();
            return true;
        }
        public bool GetSourceProperty(EFXSourceFloat param, out float value)
        {
            var eff = ParentListener.Effects?.Api;
            if (eff is null)
            {
                value = 0;
                return false;
            }

            eff.GetSourceProperty(Handle, param, out value);
            ParentListener.VerifyError();
            return true;
        }
        public bool GetSourceProperty(EFXSourceBoolean param, out bool value)
        {
            var eff = ParentListener.Effects?.Api;
            if (eff is null)
            {
                value = false;
                return false;
            }

            eff.GetSourceProperty(Handle, param, out value);
            ParentListener.VerifyError();
            return true;
        }
        public bool GetSourceProperty(EFXSourceInteger3 param, out int x, out int y, out int z)
        {
            var eff = ParentListener.Effects?.Api;
            if (eff is null)
            {
                x = y = z = 0;
                return false;
            }

            eff.GetSourceProperty(Handle, param, out x, out y, out z);
            ParentListener.VerifyError();
            return true;
        }
        public void SetSourceProperty(EFXSourceInteger param, int value)
        {
            var eff = ParentListener.Effects?.Api;
            if (eff is null)
                return;

            eff.SetSourceProperty(Handle, param, value);
            ParentListener.VerifyError();
        }
        public void SetSourceProperty(EFXSourceFloat param, float value)
        {
            var eff = ParentListener.Effects?.Api;
            if (eff is null)
                return;

            eff.SetSourceProperty(Handle, param, value);
            ParentListener.VerifyError();
        }
        public void SetSourceProperty(EFXSourceBoolean param, bool value)
        {
            var eff = ParentListener.Effects?.Api;
            if (eff is null)
                return;

            eff.SetSourceProperty(Handle, param, value);
            ParentListener.VerifyError();
        }
        public void SetSourceProperty(EFXSourceInteger3 param, int x, int y, int z)
        {
            var eff = ParentListener.Effects?.Api;
            if (eff is null)
                return;

            eff.SetSourceProperty(Handle, param, x, y, z);
            ParentListener.VerifyError();
        }
        #endregion

        void IPoolable.OnPoolableReset()
        {
            _currentStreamingBuffers.Clear();
            _bufferHandle = AudioBufferHandle.Invalid;
            _sourceState = ESourceState.Initial;
            _sourceType = ESourceType.Undetermined;
            CreateNativeSource();
        }

        void IPoolable.OnPoolableReleased()
        {
            ReleaseAllTrackedStreamingBuffers();
            DestroyNativeSource();
        }

        void IPoolable.OnPoolableDestroyed()
        {
            Dispose();
        }
    }
}