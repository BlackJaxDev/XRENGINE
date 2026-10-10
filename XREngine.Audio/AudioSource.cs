using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Numerics;
using XREngine.Core;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Audio
{
    public sealed class AudioSource : IDisposable, IPoolable, IAudioPlaybackSource
    {
        private bool IsV2 => ParentListener.IsV2 && ParentListener.Transport is not null;
        private IAudioListenerBackend LegacyBackend => ParentListener.ActiveTransport as IAudioListenerBackend
            ?? throw new InvalidOperationException("The listener transport does not support legacy source controls.");

        internal AudioSource(ListenerContext parentListener)
        {
            ParentListener = parentListener;
        }

        public ListenerContext ParentListener { get; }
        private AudioSourceHandle _transportHandle;
        private EffectsSourceHandle? _effectsHandle;
        private int _sourceLifetimeVersion;
        private int _streamingOwnershipVersion;
        private int _playbackCommandVersion;

        public uint Handle => _transportHandle.Id;
        internal AudioSourceHandle TransportHandle => _transportHandle;
        internal EffectsSourceHandle? EffectsHandle => _effectsHandle;

        private void CreateNativeSource()
        {
            _transportHandle = ParentListener.ActiveTransport.CreateSource();
            unchecked { _sourceLifetimeVersion++; _streamingOwnershipVersion++; }
            if (IsV2)
                RegisterEffectsSource();
        }

        private void DestroyNativeSource()
        {
            if (!_transportHandle.IsValid)
                return;

            if (IsV2)
                UnregisterEffectsSource();
            if (ParentListener.ActiveTransport.IsOpen)
                ParentListener.ActiveTransport.DestroySource(_transportHandle);
            _transportHandle = AudioSourceHandle.Invalid;
            unchecked { _sourceLifetimeVersion++; _streamingOwnershipVersion++; }
        }

        private void RegisterEffectsSource()
        {
            UnregisterEffectsSource();

            var processor = ParentListener.EffectsProcessor;
            if (processor is null)
                return;
            if (processor is ISpatialAudioEffectsProcessor && _bypassSteamAudioSpatialization)
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
            ReleaseAllTrackedStreamingBuffers();

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
                {
                    if (!ReferenceEquals(value.ParentListener, ParentListener))
                        throw new InvalidOperationException("A source cannot attach a buffer from another listener.");
                    SetBufferHandle(value.Handle);
                }
                else
                    SetBufferHandle(0);
            }
        }

        private readonly Queue<AudioBuffer> _currentStreamingBuffers = [];
        private readonly Queue<(AudioBuffer Buffer, int Lease, bool Notify, uint Source, int Lifetime)> _retiredStreamingBuffers = new(32);
        private bool _drainingStreamingRetirements;
        public Queue<AudioBuffer> CurrentStreamingBuffers => _currentStreamingBuffers;

        public XREvent<AudioBuffer>? BufferQueued;
        public XREvent<AudioBuffer>? BufferProcessed;
        public event Action? StreamingBufferProcessed;

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

        private bool TryGetSteamAudioProcessor(out ISpatialAudioEffectsProcessor processor, out EffectsSourceHandle effectsHandle)
        {
            processor = null!;
            effectsHandle = EffectsSourceHandle.Invalid;

            if (_bypassSteamAudioSpatialization)
                return false;

            if (!IsV2 || ParentListener.EffectsProcessor is not ISpatialAudioEffectsProcessor steamProcessor)
                return false;

            if (_effectsHandle is not { } handle || !handle.IsValid)
                return false;

            processor = steamProcessor;
            effectsHandle = handle;
            return true;
        }

        private static float[] ProcessSteamAudioBuffer(ISpatialAudioEffectsProcessor processor, EffectsSourceHandle effectsHandle, float[] input, int frequency, int inputChannels)
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

        public bool QueueBuffers(int maxbuffers, params AudioBuffer[] buffers)
        {
            foreach (AudioBuffer buffer in buffers)
            {
                if (!ReferenceEquals(buffer.ParentListener, ParentListener))
                    throw new InvalidOperationException("A source cannot queue a buffer from another listener.");
            }

            int buffersProcessed = BuffersProcessed;
            if (buffersProcessed > 0)
                UnqueueConsumedBuffers(buffersProcessed);
            
            if (buffers.Length > maxbuffers || BuffersQueued > maxbuffers - buffers.Length)
            {
                // Return the passed-in buffers to the pool so they aren't leaked.
                foreach (var leaked in buffers)
                    ParentListener.ReleaseBuffer(leaked);
                AudioDiagnostics.RecordBufferOverflow(Handle, buffers.Length);
                return false;
            }

            Span<AudioBufferHandle> queueHandles = stackalloc AudioBufferHandle[buffers.Length];
            for (int i = 0; i < buffers.Length; i++)
            {
                queueHandles[i] = buffers[i].TransportHandle;
            }
            // Publish managed ownership only after the complete transport batch is admitted.
            _currentStreamingBuffers.EnsureCapacity(checked(_currentStreamingBuffers.Count + buffers.Length));
            ParentListener.ActiveTransport.QueueBuffers(_transportHandle, queueHandles);
            for (int i = 0; i < buffers.Length; i++)
                _currentStreamingBuffers.Enqueue(buffers[i]);
            unchecked { _streamingOwnershipVersion++; }
            if (IsV2)
                _sourceType = ESourceType.Streaming;
            AudioDiagnostics.RecordBuffersQueued(Handle, buffers.Length, BuffersQueued);

            int ownershipVersion = _streamingOwnershipVersion;
            int commandVersion = _playbackCommandVersion;
            for (int i = 0; i < buffers.Length; i++)
            {
                BufferQueued?.Invoke(buffers[i]);
                // A callback may unqueue/release the batch, replace the buffer, issue a
                // playback command, or destroy/reuse this source. Never notify retired
                // references or override the callback's explicit playback decision.
                if (ownershipVersion != _streamingOwnershipVersion || commandVersion != _playbackCommandVersion)
                    return true;
            }

            if (AutoPlayOnQueue && !IsPlaying)
            {
                Play();
            }
            return true;
        }
        public void UnqueueConsumedBuffers(int requestedCount = 0)
        {
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

            _retiredStreamingBuffers.EnsureCapacity(checked(_retiredStreamingBuffers.Count + count));
            Span<AudioBufferHandle> unqueued = stackalloc AudioBufferHandle[count];
            count = Math.Min(count, ParentListener.ActiveTransport.UnqueueProcessedBuffers(_transportHandle, unqueued));
            if (count == 0)
                return;

            unchecked { _streamingOwnershipVersion++; }
            uint sourceHandle = Handle;
            // Retire the complete native batch before any observer can throw or re-enter.
            for (int i = 0; i < count; i++)
            {
                AudioBuffer? buffer = RemoveTrackedStreamingBuffer(unqueued[i].Id);
                if (buffer is not null)
                {
                    buffer.BeginStreamingRetirement();
                    _retiredStreamingBuffers.Enqueue((buffer, buffer.PoolLeaseVersion, true, sourceHandle,
                        _sourceLifetimeVersion));
                }
            }
            AudioDiagnostics.RecordBuffersUnqueued(sourceHandle, count, _currentStreamingBuffers.Count);
            DrainStreamingRetirements();
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

        private void ReleaseAllTrackedStreamingBuffers(uint retainedBuffer = 0)
        {
            int count = _currentStreamingBuffers.Count;
            if (count == 0)
                return;
            _retiredStreamingBuffers.EnsureCapacity(checked(_retiredStreamingBuffers.Count + count));
            unchecked { _streamingOwnershipVersion++; }
            while (_currentStreamingBuffers.Count > 0)
            {
                AudioBuffer buffer = _currentStreamingBuffers.Dequeue();
                if (buffer.Handle != retainedBuffer)
                {
                    buffer.BeginStreamingRetirement();
                    _retiredStreamingBuffers.Enqueue((buffer, buffer.PoolLeaseVersion, false, 0, 0));
                }
            }
            DrainStreamingRetirements();
        }

        private bool CanNotifyStreamingRetirement(
            in (AudioBuffer Buffer, int Lease, bool Notify, uint Source, int Lifetime) retirement,
            bool hasFailure)
            => retirement.Notify && !hasFailure && retirement.Source == Handle &&
                retirement.Lifetime == _sourceLifetimeVersion &&
                retirement.Buffer.PoolLeaseVersion == retirement.Lease && ParentListener.ActiveTransport.IsOpen &&
                ReferenceEquals(ParentListener.GetBufferByHandle(retirement.Buffer.Handle), retirement.Buffer);

        private void DrainStreamingRetirements()
        {
            // Reentrant detaches/unqueues append their already-retired ownership to this
            // queue. The outer drain remains responsible for every accepted buffer.
            if (_drainingStreamingRetirements)
                return;
            _drainingStreamingRetirements = true;
            Exception? failure = null;
            try
            {
                while (_retiredStreamingBuffers.Count > 0)
                {
                    var retirement = _retiredStreamingBuffers.Dequeue();
                    AudioBuffer buffer = retirement.Buffer;
                    try
                    {
                        if (CanNotifyStreamingRetirement(in retirement, failure is not null))
                        {
                            BufferProcessed?.Invoke(buffer);
                            if (CanNotifyStreamingRetirement(in retirement, failure is not null))
                                StreamingBufferProcessed?.Invoke();
                        }
                    }
                    catch (Exception error)
                    {
                        failure = failure is null ? error : new AggregateException(
                            "Streaming buffer notification or retirement failed.", failure, error);
                    }
                    finally
                    {
                        // A callback may have pooled/reused this object, disposed its
                        // listener, or deliberately attached it as the static buffer.
                        bool lastRetirement = buffer.CompleteStreamingRetirement(retirement.Lease);
                        if (lastRetirement && !HasLiveBufferOwner(buffer) &&
                            ReferenceEquals(ParentListener.GetBufferByHandle(buffer.Handle), buffer))
                        {
                            try { ParentListener.ReleaseBuffer(buffer); }
                            catch (Exception error)
                            {
                                failure = failure is null ? error : new AggregateException(
                                    "Streaming buffer notification or retirement failed.", failure, error);
                            }
                        }
                    }
                }
            }
            finally { _drainingStreamingRetirements = false; }
            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private bool HasLiveBufferOwner(AudioBuffer buffer)
        {
            if (!ParentListener.ActiveTransport.IsOpen)
                return false;
            // Both dictionary and queue enumerators are value types. A callback can
            // transfer a retired buffer to any source without returning it to the pool.
            foreach (KeyValuePair<uint, AudioSource> pair in ParentListener.Sources)
            {
                AudioSource source = pair.Value;
                if (!source._transportHandle.IsValid)
                    continue;
                if (source._bufferHandle.Id == buffer.Handle)
                    return true;
                foreach (AudioBuffer queued in source._currentStreamingBuffers)
                    if (ReferenceEquals(queued, buffer))
                        return true;
            }
            return false;
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
                return LegacyBackend.GetSourceState(_transportHandle);
            }
        }
        /// <summary>
        /// Plays the source.
        /// </summary>
        public void Play()
        {
            var prev = SourceState;
            ParentListener.ActiveTransport.Play(_transportHandle);
            unchecked { _playbackCommandVersion++; }
            _sourceState = ESourceState.Playing;
            AudioDiagnostics.RecordSourceStateChange(Handle, prev.ToString(), nameof(ESourceState.Playing));
        }
        /// <summary>
        /// Stops the source from playing.
        /// </summary>
        public void Stop()
        {
            var prev = SourceState;
            ParentListener.ActiveTransport.Stop(_transportHandle);
            unchecked { _playbackCommandVersion++; }
            _sourceState = ESourceState.Stopped;
            AudioDiagnostics.RecordSourceStateChange(Handle, prev.ToString(), nameof(ESourceState.Stopped));
        }
        /// <summary>
        /// Pauses the source.
        /// </summary>
        public void Pause()
        {
            var prev = SourceState;
            ParentListener.ActiveTransport.Pause(_transportHandle);
            unchecked { _playbackCommandVersion++; }
            _sourceState = ESourceState.Paused;
            AudioDiagnostics.RecordSourceStateChange(Handle, prev.ToString(), nameof(ESourceState.Paused));
        }
        /// <summary>
        /// Sets the source to play from the beginning (initial state).
        /// </summary>
        public void Rewind()
        {
            var prev = SourceState;
            ParentListener.ActiveTransport.Rewind(_transportHandle);
            unchecked { _playbackCommandVersion++; }
            if (IsV2)
            {
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
            get => IsV2 ? _sourceType : LegacyBackend.GetSourceType(_transportHandle);
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

        private void SetSpatialProperty(AudioSourceFloatProperty property, float value)
        {
            if (IsV2 && ParentListener.Transport is IAudioSpatialTransport spatial)
                spatial.SetSourceProperty(_transportHandle, property, value);
            else if (ParentListener.ActiveTransport is IAudioListenerBackend backend)
                backend.SetSourceProperty(_transportHandle, property, value);
        }
        private void SetSpatialProperty(AudioSourceIntegerProperty property, int value)
        {
            if (IsV2 && ParentListener.Transport is IAudioSpatialTransport spatial)
                spatial.SetSourceProperty(_transportHandle, property, value);
            else if (ParentListener.ActiveTransport is IAudioListenerBackend backend)
                backend.SetSourceProperty(_transportHandle, property, value);
        }
        private void SetSpatialProperty(AudioSourceBooleanProperty property, bool value)
        {
            if (IsV2 && ParentListener.Transport is IAudioSpatialTransport spatial)
                spatial.SetSourceProperty(_transportHandle, property, value);
            else if (ParentListener.ActiveTransport is IAudioListenerBackend backend)
                backend.SetSourceProperty(_transportHandle, property, value);
        }
        private void SetSpatialProperty(AudioSourceVectorProperty property, Vector3 value)
        {
            if (IsV2 && ParentListener.Transport is IAudioSpatialTransport spatial)
                spatial.SetSourceProperty(_transportHandle, property, value);
            else if (ParentListener.ActiveTransport is IAudioListenerBackend backend)
                backend.SetSourceProperty(_transportHandle, property, value);
        }

        private bool GetSourceRelative()
        {
            if (IsV2)
                return _sourceRelative;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceBooleanProperty.RelativeToListener);
        }
        private bool GetLooping()
        {
            if (IsV2)
                return _looping;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceBooleanProperty.Looping);
        }
        private void SetSourceRelative(bool relative)
        {
            SetSpatialProperty(AudioSourceBooleanProperty.RelativeToListener, relative);
            _sourceRelative = relative;
        }
        private void SetLooping(bool loop)
        {
            ParentListener.ActiveTransport.SetSourceLooping(_transportHandle, loop);
            _looping = loop;
        }
        private void SetBypassSteamAudioSpatialization(bool bypass)
        {
            if (_bypassSteamAudioSpatialization == bypass)
                return;

            _bypassSteamAudioSpatialization = bypass;

            if (!IsV2 || ParentListener.EffectsProcessor is not ISpatialAudioEffectsProcessor)
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

            if (!IsV2 || ParentListener.EffectsProcessor is not ISpatialAudioEffectsProcessor || _bypassSteamAudioSpatialization)
                return;

            if (_transportHandle.IsValid)
            {
                UnregisterEffectsSource();
                RegisterEffectsSource();
            }
        }

        private int GetByteOffset()
        {
            if (IsV2 && ParentListener.Transport is IAudioSpatialTransport spatial)
                return spatial.GetSourceProperty(_transportHandle, AudioSourceIntegerProperty.ByteOffset);
            if (IsV2 && ParentListener.Transport is not IAudioListenerBackend)
                return _byteOffset;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceIntegerProperty.ByteOffset);
        }
        private int GetSampleOffset()
        {
            if (IsV2)
                return ParentListener.Transport!.GetSampleOffset(_transportHandle);
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceIntegerProperty.SampleOffset);
        }
        private uint GetBufferHandle()
        {
            if (IsV2)
                return _bufferHandle.Id;
            return LegacyBackend.GetSourceBuffer(_transportHandle).Id;
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
            return (int)LegacyBackend.GetSourceState(_transportHandle);
        }
        private int GetBuffersQueued()
        {
            if (IsV2)
                return _currentStreamingBuffers.Count;
            return ParentListener.ActiveTransport.GetBuffersQueued(_transportHandle);
        }
        private int GetBuffersProcessed()
        {
            if (IsV2)
                return ParentListener.Transport!.GetBuffersProcessed(_transportHandle);
            return ParentListener.ActiveTransport.GetBuffersProcessed(_transportHandle);
        }

        private void SetByteOffset(int offset)
        {
            SetSpatialProperty(AudioSourceIntegerProperty.ByteOffset, offset);
            _byteOffset = offset;
        }
        private void SetSampleOffset(int offset)
        {
            SetSpatialProperty(AudioSourceIntegerProperty.SampleOffset, offset);
            _sampleOffset = offset;
        }
        private void SetBufferHandle(uint buffer)
        {
            AudioBufferHandle handle = new(buffer);
            _retiredStreamingBuffers.EnsureCapacity(checked(_retiredStreamingBuffers.Count + _currentStreamingBuffers.Count));
            ParentListener.ActiveTransport.SetSourceBuffer(_transportHandle, handle);
            _bufferHandle = handle;
            if (IsV2)
            {
                _sourceType = buffer == 0 ? ESourceType.Undetermined : ESourceType.Static;
            }
            unchecked { _streamingOwnershipVersion++; }
            ReleaseAllTrackedStreamingBuffers(buffer);
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
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceVectorProperty.Position);
        }
        private Vector3 GetVelocity()
        {
            if (IsV2)
                return _velocity;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceVectorProperty.Velocity);
        }
        private Vector3 GetDirection()
        {
            if (IsV2)
                return _direction;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceVectorProperty.Direction);
        }

        private void SetPosition(Vector3 position)
        {
            ParentListener.ActiveTransport.SetSourcePosition(_transportHandle, position);
            _position = position;
            if (IsV2)
                SyncEffectsSourcePose();
        }
        private void SetVelocity(Vector3 velocity)
        {
            ParentListener.ActiveTransport.SetSourceVelocity(_transportHandle, velocity);
            _velocity = velocity;
        }
        private void SetDirection(Vector3 direction)
        {
            SetSpatialProperty(AudioSourceVectorProperty.Direction, direction);
            _direction = direction;
            if (IsV2)
                SyncEffectsSourcePose();
        }

        private float GetReferenceDistance()
        {
            if (IsV2)
                return _referenceDistance;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceFloatProperty.ReferenceDistance);
        }
        private float GetMaxDistance()
        {
            if (IsV2)
                return _maxDistance;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceFloatProperty.MaxDistance);
        }
        private float GetRolloffFactor()
        {
            if (IsV2)
                return _rolloffFactor;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceFloatProperty.RolloffFactor);
        }
        private float GetPitch()
        {
            if (IsV2)
                return _pitch;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceFloatProperty.Pitch);
        }
        private float GetMinGain()
        {
            if (IsV2)
                return _minGain;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceFloatProperty.MinGain);
        }
        private float GetMaxGain()
        {
            if (IsV2)
                return _maxGain;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceFloatProperty.MaxGain);
        }
        private float GetGain()
        {
            if (IsV2)
                return _gain;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceFloatProperty.Gain);
        }
        private float GetConeInnerAngle()
        {
            if (IsV2)
                return _coneInnerAngle;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceFloatProperty.ConeInnerAngle);
        }
        private float GetConeOuterAngle()
        {
            if (IsV2)
                return _coneOuterAngle;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceFloatProperty.ConeOuterAngle);
        }
        private float GetConeOuterGain()
        {
            if (IsV2)
                return _coneOuterGain;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceFloatProperty.ConeOuterGain);
        }
        private float GetSecOffset()
        {
            if (IsV2 && ParentListener.Transport is IAudioSpatialTransport spatial)
                return spatial.GetSourceProperty(_transportHandle, AudioSourceFloatProperty.SecondsOffset);
            if (IsV2 && ParentListener.Transport is not IAudioListenerBackend)
                return _secondsOffset;
            return LegacyBackend.GetSourceProperty(_transportHandle, AudioSourceFloatProperty.SecondsOffset);
        }

        private void SetReferenceDistance(float distance)
        {
            SetSpatialProperty(AudioSourceFloatProperty.ReferenceDistance, distance);
            _referenceDistance = distance;
        }
        private void SetMaxDistance(float distance)
        {
            SetSpatialProperty(AudioSourceFloatProperty.MaxDistance, distance);
            _maxDistance = distance;
        }
        private void SetRolloffFactor(float factor)
        {
            SetSpatialProperty(AudioSourceFloatProperty.RolloffFactor, factor);
            _rolloffFactor = factor;
        }
        private void SetPitch(float pitch)
        {
            ParentListener.ActiveTransport.SetSourcePitch(_transportHandle, pitch);
            _pitch = pitch;
        }
        private void SetMinGain(float gain)
        {
            SetSpatialProperty(AudioSourceFloatProperty.MinGain, gain);
            _minGain = gain;
        }
        private void SetMaxGain(float gain)
        {
            SetSpatialProperty(AudioSourceFloatProperty.MaxGain, gain);
            _maxGain = gain;
        }
        private void SetGain(float gain)
        {
            ParentListener.ActiveTransport.SetSourceGain(_transportHandle, gain);
            _gain = gain;
        }
        private void SetConeInnerAngle(float angle)
        {
            SetSpatialProperty(AudioSourceFloatProperty.ConeInnerAngle, angle);
            _coneInnerAngle = angle;
        }
        private void SetConeOuterAngle(float angle)
        {
            SetSpatialProperty(AudioSourceFloatProperty.ConeOuterAngle, angle);
            _coneOuterAngle = angle;
        }
        private void SetConeOuterGain(float gain)
        {
            SetSpatialProperty(AudioSourceFloatProperty.ConeOuterGain, gain);
            _coneOuterGain = gain;
        }
        private void SetSecOffset(float offset)
        {
            SetSpatialProperty(AudioSourceFloatProperty.SecondsOffset, offset);
            _secondsOffset = offset;
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
get => GetSourceProperty(AudioEffectBooleanProperty.DirectFilterGainHighFrequencyAuto, out bool value) && value;
            set => SetSourceProperty(AudioEffectBooleanProperty.DirectFilterGainHighFrequencyAuto, value);
        }
        public bool AuxiliarySendFilterGainAuto
        {
            get => GetSourceProperty(AudioEffectBooleanProperty.AuxiliarySendFilterGainAuto, out bool value) && value;
            set => SetSourceProperty(AudioEffectBooleanProperty.AuxiliarySendFilterGainAuto, value);
        }
        public bool AuxiliarySendFilterGainHighFrequencyAuto
        {
            get => GetSourceProperty(AudioEffectBooleanProperty.AuxiliarySendFilterGainHighFrequencyAuto, out bool value) && value;
            set => SetSourceProperty(AudioEffectBooleanProperty.AuxiliarySendFilterGainHighFrequencyAuto, value);
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
            GetSourceProperty(AudioEffectTripleProperty.AuxiliarySendFilter, out int slotID, out int sendNumber, out int filterID);
            return new AuxSendFilter { AuxEffectSlotID = slotID, AuxSendNumber = sendNumber, FilterID = filterID };
        }
        public void SetAuxiliarySendFilter(int slotID, int sendNumber, int filterID)
        {
            SetSourceProperty(AudioEffectTripleProperty.AuxiliarySendFilter, slotID, sendNumber, filterID);
        }
        private void SetDirectFilter(int value)
        {
            SetSourceProperty(AudioEffectIntegerProperty.DirectFilter, value);
        }
        private int GetDirectFilter()
        {
            GetSourceProperty(AudioEffectIntegerProperty.DirectFilter, out int value);
            return value;
        }
        private void SetAirAbsorptionFactor(float value)
        {
            SetSourceProperty(AudioEffectFloatProperty.AirAbsorptionFactor, value);
        }
        private float GetAirAbsorptionFactor()
        {
            GetSourceProperty(AudioEffectFloatProperty.AirAbsorptionFactor, out float value);
            return value;
        }
        private void SetRoomRolloffFactor(float value)
        {
            SetSourceProperty(AudioEffectFloatProperty.RoomRolloffFactor, value);
        }
        private float GetRoomRolloffFactor()
        {
            GetSourceProperty(AudioEffectFloatProperty.RoomRolloffFactor, out float value);
            return value;
        }
        private void SetConeOuterGainHF(float value)
        {
            SetSourceProperty(AudioEffectFloatProperty.ConeOuterGainHighFrequency, value);
        }
        private float GetConeOuterGainHF()
        {
            GetSourceProperty(AudioEffectFloatProperty.ConeOuterGainHighFrequency, out float value);
            return value;
        }
        public bool GetSourceProperty(AudioEffectIntegerProperty param, out int value)
        {
            var eff = ParentListener.Effects;
            if (eff is null)
            {
                value = 0;
                return false;
            }
            value = eff.GetSourceProperty(_transportHandle, param);
            return true;
        }
        public bool GetSourceProperty(AudioEffectFloatProperty param, out float value)
        {
            var eff = ParentListener.Effects;
            if (eff is null)
            {
                value = 0;
                return false;
            }
            value = eff.GetSourceProperty(_transportHandle, param);
            return true;
        }
        public bool GetSourceProperty(AudioEffectBooleanProperty param, out bool value)
        {
            var eff = ParentListener.Effects;
            if (eff is null)
            {
                value = false;
                return false;
            }
            value = eff.GetSourceProperty(_transportHandle, param);
            return true;
        }
        public bool GetSourceProperty(AudioEffectTripleProperty param, out int x, out int y, out int z)
        {
            var eff = ParentListener.Effects;
            if (eff is null)
            {
                x = y = z = 0;
                return false;
            }
            eff.GetSourceProperty(_transportHandle, param, out x, out y, out z);
            return true;
        }
        public void SetSourceProperty(AudioEffectIntegerProperty param, int value)
        {
            ParentListener.Effects?.SetSourceProperty(_transportHandle, param, value);
        }
        public void SetSourceProperty(AudioEffectFloatProperty param, float value)
        {
            ParentListener.Effects?.SetSourceProperty(_transportHandle, param, value);
        }
        public void SetSourceProperty(AudioEffectBooleanProperty param, bool value)
        {
            ParentListener.Effects?.SetSourceProperty(_transportHandle, param, value);
        }
        public void SetSourceProperty(AudioEffectTripleProperty param, int x, int y, int z)
        {
            ParentListener.Effects?.SetSourceProperty(_transportHandle, param, x, y, z);
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
            DestroyNativeSource();
            ReleaseAllTrackedStreamingBuffers();
        }

        void IPoolable.OnPoolableDestroyed()
        {
            Dispose();
        }
    }
}
