using MathNet.Numerics.IntegralTransforms;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;
using System.Numerics;
using XREngine.Core;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Audio
{
    public sealed class AudioBuffer : XRBase, IDisposable, IPoolable
    {
        public ListenerContext ParentListener { get; }
        public AL Api { get; }
        public uint Handle { get; private set; }

        internal AudioBuffer(ListenerContext parentListener)
        {
            ParentListener = parentListener;
            Api = parentListener.Api;
            Handle = Api.GenBuffer();
            ParentListener.VerifyError();
        }

        private object? _data;
        private int _freq;
        private bool _stereo;

        public object? Data => _data;
        public int Frequency => _freq;
        public bool Stereo => _stereo;

        public unsafe void SetData(byte[] data, int frequency, bool stereo)
        {
            _data = data;
            _freq = frequency;
            _stereo = stereo;

            Api.BufferData(Handle, stereo ? BufferFormat.Stereo8 : BufferFormat.Mono8, data, frequency);
            ParentListener.VerifyError();
        }
        public void SetData(short[] data, int frequency, bool stereo)
        {
            _data = data;
            _freq = frequency;
            _stereo = stereo;

            Api.BufferData(Handle, stereo ? BufferFormat.Stereo16 : BufferFormat.Mono16, data, frequency);
            ParentListener.VerifyError();
        }
        public void SetData(float[] data, int frequency, bool stereo)
        {
            _data = data;
            _freq = frequency;
            _stereo = stereo;

            Api.BufferData(Handle, stereo ? FloatBufferFormat.Stereo : FloatBufferFormat.Mono, data, frequency);
            ParentListener.VerifyError();
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
            ParentListener.VerifyError();
            switch (buffer.Type)
            {
                case AudioData.EPCMType.Byte:
                    Api.BufferData(Handle, buffer.Stereo ? BufferFormat.Stereo8 : BufferFormat.Mono8, ptr, length, buffer.Frequency);
                    break;
                case AudioData.EPCMType.Short:
                    Api.BufferData(Handle, buffer.Stereo ? BufferFormat.Stereo16 : BufferFormat.Mono16, ptr, length, buffer.Frequency);
                    break;
                case AudioData.EPCMType.Float:
                    Api.BufferData(Handle, buffer.Stereo ? FloatBufferFormat.Stereo : FloatBufferFormat.Mono, ptr, length, buffer.Frequency);
                    break;
            }
            ParentListener.VerifyError();
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
            if (Handle == 0)
                return;
            Api.DeleteBuffer(Handle);
            ParentListener.VerifyError();
            Handle = 0;
        }

        void IPoolable.OnPoolableReset()
        {
            //if (Handle != 0)
            //{
            //    Api.DeleteBuffer(Handle);
            //    ParentListener.VerifyError();
            //    Handle = 0;
            //}

            //Handle = Api.GenBuffer();
            //ParentListener.VerifyError();
            //The user should call SetData to set the data for the buffer after taking it from the pool.
        }

        void IPoolable.OnPoolableReleased()
        {
            //SetData(Array.Empty<byte>(), 0, false);

            //if (Handle == 0)
            //    return;
            //Api.DeleteBuffer(Handle);
            //ParentListener.VerifyError();
            //Handle = 0;
        }

        void IPoolable.OnPoolableDestroyed()
        {
            Dispose();
        }
    }
}