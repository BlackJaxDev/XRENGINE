using MathNet.Numerics.IntegralTransforms;
using System.Numerics;
using XREngine.Core;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Audio
{
    public sealed class AudioBuffer : XRBase, IDisposable, IPoolable
    {
        public ListenerContext ParentListener { get; }
        private AudioBufferHandle _transportHandle;
        private int _poolLeaseVersion;
        private int _retirementReferences;
        internal int PoolLeaseVersion => _poolLeaseVersion;

        internal void BeginStreamingRetirement() => _retirementReferences++;
        internal bool CompleteStreamingRetirement(int lease)
        {
            if (lease != _poolLeaseVersion)
                return false;
            return --_retirementReferences == 0;
        }

        public uint Handle => _transportHandle.Id;
        internal AudioBufferHandle TransportHandle => _transportHandle;

        internal AudioBuffer(ListenerContext parentListener)
        {
            ParentListener = parentListener;
            CreateNativeBuffer();
        }

        private void CreateNativeBuffer()
        {
            _transportHandle = ParentListener.ActiveTransport.CreateBuffer();
        }

        private void DestroyNativeBuffer()
        {
            if (!_transportHandle.IsValid)
                return;

            if (ParentListener.ActiveTransport.IsOpen)
                ParentListener.ActiveTransport.DestroyBuffer(_transportHandle);

            _transportHandle = AudioBufferHandle.Invalid;
        }

        private object? _data;
        private int _freq;
        private bool _stereo;

        public object? Data => _data;
        public int Frequency => _freq;
        public bool Stereo => _stereo;

        public void SetData(byte[] data, int frequency, bool stereo)
        {
            _data = data;
            _freq = frequency;
            _stereo = stereo;

            ParentListener.ActiveTransport.UploadBufferData(
                _transportHandle, data, frequency, stereo ? 2 : 1, SampleFormat.Byte);
        }
        public void SetData(short[] data, int frequency, bool stereo)
        {
            _data = data;
            _freq = frequency;
            _stereo = stereo;

            ReadOnlySpan<byte> pcm = System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan());
            ParentListener.ActiveTransport.UploadBufferData(
                _transportHandle, pcm, frequency, stereo ? 2 : 1, SampleFormat.Short);
        }
        public void SetData(float[] data, int frequency, bool stereo)
        {
            _data = data;
            _freq = frequency;
            _stereo = stereo;

            ReadOnlySpan<byte> pcm = System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan());
            ParentListener.ActiveTransport.UploadBufferData(
                _transportHandle, pcm, frequency, stereo ? 2 : 1, SampleFormat.Float);
        }

        public unsafe void SetData(AudioData buffer)
        {
            if (buffer.Data is null)
                return;

            _data = buffer.Data;
            _freq = buffer.Frequency;
            _stereo = buffer.Stereo;

            void* ptr = buffer.Data.Address.Pointer;
            int length = (int)buffer.Data.Length;
            var pcm = new ReadOnlySpan<byte>(ptr, length);
            SampleFormat format = buffer.Type switch
            {
                AudioData.EPCMType.Byte => SampleFormat.Byte,
                AudioData.EPCMType.Short => SampleFormat.Short,
                AudioData.EPCMType.Float => SampleFormat.Float,
                _ => throw new ArgumentOutOfRangeException(nameof(buffer), buffer.Type, "Unsupported PCM sample type."),
            };
            ParentListener.ActiveTransport.UploadBufferData(
                _transportHandle, pcm, buffer.Frequency, buffer.Stereo ? 2 : 1, format);
        }

        /// <summary>
        /// How magnitude values are accumulated for each frequency band.
        /// This will affect how the strengths of each band appear.
        /// </summary>
        public enum EMagAccumMethod
        {
            Max,
            Average,
            Sum
        }

        /// <summary>
        /// Calculates the strength of the bass, mids, and treble frequencies in the audio buffer.
        /// </summary>
        /// <param name="samples"></param>
        /// <param name="sampleRate"></param>
        /// <param name="bass"></param>
        /// <param name="mids"></param>
        /// <param name="treble"></param>
        /// <returns></returns>
        public static (float bass, float mids, float treble) FastFourier(
            float[] samples,
            int sampleRate,
            (float upperRange, EMagAccumMethod accum) bass,
            (float upperRange, EMagAccumMethod accum) mids,
            (float upperRange, EMagAccumMethod accum) treble)
        {
            int sampleCount = samples.Length;
            Complex[] complexBuffer = [.. samples.Select(x => new Complex(x, 0.0))];
            Fourier.Forward(complexBuffer, FourierOptions.Matlab);

            // Analyze the frequency bands
            float bassStrength = 0;
            float midsStrength = 0;
            float trebleStrength = 0;
            int bassCount = 0;
            int midsCount = 0;
            int trebleCount = 0;
            float maxBass = 0;
            float maxMids = 0;
            float maxTreble = 0;

            // FFT output gives us frequency bins, we need to find which bins correspond to bass, mids, treble
            float binSize = (float)sampleRate / sampleCount;

            for (int i = 0; i < complexBuffer.Length / 2; i++)
            {
                float magnitude = (float)complexBuffer[i].Magnitude;
                float frequency = i * binSize;
                if (frequency <= bass.upperRange)
                {
                    if (bass.accum != EMagAccumMethod.Max)
                    {
                        bassStrength += magnitude;
                        if (bass.accum == EMagAccumMethod.Average)
                            bassCount++;
                    }
                    else
                        bassStrength = Math.Max(bassStrength, magnitude);
                }
                else if (frequency <= mids.upperRange)
                {
                    if (mids.accum != EMagAccumMethod.Max)
                    {
                        midsStrength += magnitude;
                        if (mids.accum == EMagAccumMethod.Average)
                            midsCount++;
                    }
                    else
                        midsStrength = Math.Max(midsStrength, magnitude);
                }
                else if (frequency <= treble.upperRange)
                {
                    if (treble.accum != EMagAccumMethod.Max)
                    {
                        trebleStrength += magnitude;
                        if (treble.accum == EMagAccumMethod.Average)
                            trebleCount++;
                    }
                    else
                        trebleStrength = Math.Max(trebleStrength, magnitude);
                }
            }
            switch (bass.accum)
            {
                case EMagAccumMethod.Average:
                    bassStrength /= bassCount;
                    break;
                case EMagAccumMethod.Sum:
                    break;
                case EMagAccumMethod.Max:
                    bassStrength = maxBass;
                    break;
            }
            switch (mids.accum)
            {
                case EMagAccumMethod.Average:
                    midsStrength /= midsCount;
                    break;
                case EMagAccumMethod.Sum:
                    break;
                case EMagAccumMethod.Max:
                    midsStrength = maxMids;
                    break;
            }
            switch (treble.accum)
            {
                case EMagAccumMethod.Average:
                    trebleStrength /= trebleCount;
                    break;
                case EMagAccumMethod.Sum:
                    break;
                case EMagAccumMethod.Max:
                    trebleStrength = maxTreble;
                    break;
            }
            return (bassStrength, midsStrength, trebleStrength);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            if (!_transportHandle.IsValid)
                return;

            DestroyNativeBuffer();
        }

        void IPoolable.OnPoolableReset()
        {
            unchecked { _poolLeaseVersion++; }
            // The caller should call SetData after taking a buffer from the pool.
            // No-op: reuse the existing native handle.
        }

        void IPoolable.OnPoolableReleased()
        {
            unchecked { _poolLeaseVersion++; }
            _retirementReferences = 0;
            // Clear cached data reference so we don't pin managed arrays unnecessarily.
            _data = null;
        }

        void IPoolable.OnPoolableDestroyed()
        {
            Dispose();
        }
    }
}
