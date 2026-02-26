using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;
using System.Diagnostics;
using System.Numerics;
using XREngine.Core;
using XREngine.Data.Core;

namespace XREngine.Audio
{
    public sealed class AudioSource : IDisposable, IPoolable
    {
        internal AudioSource(ListenerContext parentListener)
        {
            ParentListener = parentListener;
            Api = parentListener.Api;
            Handle = Api.GenSource();
            ParentListener.VerifyError();
        }

        public ListenerContext ParentListener { get; }
        public AL Api { get; }
        public uint Handle { get; private set; }

        public void Dispose()
        {
            if (Handle == 0u)
                return;

            Api.SourceStop(Handle);
            Api.DeleteSource(Handle);
            Handle = 0u;

            GC.SuppressFinalize(this);
        }

        #region Buffers
        /// <summary>
        /// The number of buffers queued on this source.
        /// </summary>
        public int BuffersQueued
            => GetBuffersQueued();
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
            get => ParentListener.GetBufferByHandle(GetBufferHandle());
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

        public unsafe bool QueueBuffers(int maxbuffers, params AudioBuffer[] buffers)
        {
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
            //Trace.WriteLineIf(handles.Length > 0, $"Queuing {handles.Length} buffers.");
            Api.SourceQueueBuffers(Handle, buffers.Length, handles);
            ParentListener.VerifyError();
            AudioDiagnostics.RecordBuffersQueued(Handle, buffers.Length, BuffersQueued);

            if (AutoPlayOnQueue && !IsPlaying)
            {
                Api.SourcePlay(Handle);
                ParentListener.VerifyError();
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
            // OpenAL fills 'handles' with the buffer IDs that were actually
            // unqueued. We must NOT pre-populate this array with our expected
            // IDs; doing so can desync source/buffer state and cause
            // AL_INVALID_VALUE during long-running streaming playback.
            Api.SourceUnqueueBuffers(Handle, count, handles);
            ParentListener.VerifyError();

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

        private static ESourceState ConvSourceState(SourceState state)
            => state switch
            {
                Silk.NET.OpenAL.SourceState.Initial => ESourceState.Initial,
                Silk.NET.OpenAL.SourceState.Playing => ESourceState.Playing,
                Silk.NET.OpenAL.SourceState.Paused => ESourceState.Paused,
                Silk.NET.OpenAL.SourceState.Stopped => ESourceState.Stopped,
                _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
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
            => ConvSourceState((SourceState)GetSourceState());
        /// <summary>
        /// Plays the source.
        /// </summary>
        public void Play()
        {
            var prev = SourceState;
            Api.SourcePlay(Handle);
            ParentListener.VerifyError();
            AudioDiagnostics.RecordSourceStateChange(Handle, prev.ToString(), nameof(ESourceState.Playing));
        }
        /// <summary>
        /// Stops the source from playing.
        /// </summary>
        public void Stop()
        {
            var prev = SourceState;
            Api.SourceStop(Handle);
            ParentListener.VerifyError();
            AudioDiagnostics.RecordSourceStateChange(Handle, prev.ToString(), nameof(ESourceState.Stopped));
        }
        /// <summary>
        /// Pauses the source.
        /// </summary>
        public void Pause()
        {
            var prev = SourceState;
            Api.SourcePause(Handle);
            ParentListener.VerifyError();
            AudioDiagnostics.RecordSourceStateChange(Handle, prev.ToString(), nameof(ESourceState.Paused));
        }
        /// <summary>
        /// Sets the source to play from the beginning (initial state).
        /// </summary>
        public void Rewind()
        {
            var prev = SourceState;
            Api.SourceRewind(Handle);
            ParentListener.VerifyError();
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
            get => ConvSourceType((SourceType)GetSourceType());
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
        private bool GetSourceRelative()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceBoolean.SourceRelative, out bool value);
            ParentListener.VerifyError();
            return value;
        }
        private bool GetLooping()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceBoolean.Looping, out bool value);
            ParentListener.VerifyError();
            return value;
        }
        private void SetSourceRelative(bool relative)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceBoolean.SourceRelative, relative);
            ParentListener.VerifyError();
        }
        private void SetLooping(bool loop)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceBoolean.Looping, loop);
            ParentListener.VerifyError();
        }

        private int GetByteOffset()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, GetSourceInteger.ByteOffset, out int value);
            ParentListener.VerifyError();
            return value;
        }
        private int GetSampleOffset()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, GetSourceInteger.SampleOffset, out int value);
            ParentListener.VerifyError();
            return value;
        }
        private unsafe uint GetBufferHandle()
        {
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
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, GetSourceInteger.SourceState, out int value);
            ParentListener.VerifyError();
            return value;
        }
        private unsafe int GetBuffersQueued()
        {
            ParentListener.MakeCurrent();
            int value;
            Api.GetSourceProperty(Handle, GetSourceInteger.BuffersQueued, &value);
            ParentListener.VerifyError();
            return value;
        }
        private unsafe int GetBuffersProcessed()
        {
            ParentListener.MakeCurrent();
            int value;
            Api.GetSourceProperty(Handle, GetSourceInteger.BuffersProcessed, &value);
            ParentListener.VerifyError();
            return value;
        }

        private void SetByteOffset(int offset)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceInteger.ByteOffset, offset);
            ParentListener.VerifyError();
        }
        private void SetSampleOffset(int offset)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceInteger.SampleOffset, offset);
            ParentListener.VerifyError();
        }
        private void SetBufferHandle(uint buffer)
        {
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
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceVector3.Position, out Vector3 value);
            ParentListener.VerifyError();
            return value;
        }
        private Vector3 GetVelocity()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceVector3.Velocity, out Vector3 value);
            ParentListener.VerifyError();
            return value;
        }
        private Vector3 GetDirection()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceVector3.Direction, out Vector3 value);
            ParentListener.VerifyError();
            return value;
        }

        private void SetPosition(Vector3 position)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceVector3.Position, position);
            ParentListener.VerifyError();
        }
        private void SetVelocity(Vector3 velocity)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceVector3.Velocity, velocity);
            ParentListener.VerifyError();
        }
        private void SetDirection(Vector3 direction)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceVector3.Direction, direction);
            ParentListener.VerifyError();
        }

        private float GetReferenceDistance()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.ReferenceDistance, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetMaxDistance()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.MaxDistance, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetRolloffFactor()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.RolloffFactor, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetPitch()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.Pitch, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetMinGain()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.MinGain, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetMaxGain()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.MaxGain, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetGain()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.Gain, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetConeInnerAngle()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.ConeInnerAngle, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetConeOuterAngle()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.ConeOuterAngle, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetConeOuterGain()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.ConeOuterGain, out float value);
            ParentListener.VerifyError();
            return value;
        }
        private float GetSecOffset()
        {
            ParentListener.MakeCurrent();
            Api.GetSourceProperty(Handle, SourceFloat.SecOffset, out float value);
            ParentListener.VerifyError();
            return value;
        }

        private void SetReferenceDistance(float distance)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.ReferenceDistance, distance);
            ParentListener.VerifyError();
        }
        private void SetMaxDistance(float distance)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.MaxDistance, distance);
            ParentListener.VerifyError();
        }
        private void SetRolloffFactor(float factor)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.RolloffFactor, factor);
            ParentListener.VerifyError();
        }
        private void SetPitch(float pitch)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.Pitch, pitch);
            ParentListener.VerifyError();
        }
        private void SetMinGain(float gain)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.MinGain, gain);
            ParentListener.VerifyError();
        }
        private void SetMaxGain(float gain)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.MaxGain, gain);
            ParentListener.VerifyError();
        }
        private void SetGain(float gain)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.Gain, gain);
            ParentListener.VerifyError();
        }
        private void SetConeInnerAngle(float angle)
        {
            Api.SetSourceProperty(Handle, SourceFloat.ConeInnerAngle, angle);
            ParentListener.VerifyError();
        }
        private void SetConeOuterAngle(float angle)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.ConeOuterAngle, angle);
            ParentListener.VerifyError();
        }
        private void SetConeOuterGain(float gain)
        {
            ParentListener.MakeCurrent();
            Api.SetSourceProperty(Handle, SourceFloat.ConeOuterGain, gain);
            ParentListener.VerifyError();
        }
        private void SetSecOffset(float offset)
        {
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
            Handle = Api.GenSource();
            ParentListener.VerifyError();
        }

        void IPoolable.OnPoolableReleased()
        {
            ReleaseAllTrackedStreamingBuffers();
            Api.SourceStop(Handle);
            Api.DeleteSource(Handle);
            Handle = 0u;
            ParentListener.VerifyError();
        }

        void IPoolable.OnPoolableDestroyed()
        {
            Dispose();
        }
    }
}