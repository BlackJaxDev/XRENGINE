using MemoryPack;
using NAudio.Wave;
using NVorbis;
using XREngine.Core;
using XREngine.Core.Files;

namespace XREngine.Data
{
    [MemoryPackable]
    [XR3rdPartyExtensions(typeof(XRDefault3rdPartyImportOptions), "wav", "ogg", "mp3", "flac")]
    public partial class AudioData : XRAsset, IPoolable
    {
        private DataSource? _data;
        private int _frequency;
        private int _channelCount;
        private EPCMType _type;

        public EPCMType Type
        {
            get => _type;
            set => SetField(ref _type, value);
        }

        public enum EPCMType
        {
            Byte,
            Short,
            Float
        }

        public DataSource? Data
        {
            get => _data;
            set => SetField(ref _data, value);
        }

        public unsafe float[] GetFloatData()
        {
            if (_data is null)
                return [];

            float* data = (float*)_data.Address;
            float[] result = new float[_data.Length / sizeof(float)];
            for (int i = 0; i < result.Length; i++)
                result[i] = data[i];

            return result;
        }
        public unsafe short[] GetShortData()
        {
            if (_data is null)
                return [];

            short* data = (short*)_data.Address;
            short[] result = new short[_data.Length / sizeof(short)];
            for (int i = 0; i < result.Length; i++)
                result[i] = data[i];

            return result;
        }
        public unsafe byte[] GetByteData()
        {
            if (_data is null)
                return [];

            byte* data = (byte*)_data.Address;
            byte[] result = new byte[_data.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = data[i];

            return result;
        }

        /// <summary>
        /// The frequency of the audio data in Hz.
        /// Also known as the sample rate.
        /// </summary>
        public int Frequency
        {
            get => _frequency;
            set => SetField(ref _frequency, value);
        }

        public bool Stereo => _channelCount == 2;
        public bool Mono => _channelCount == 1;
        public int ChannelCount
        {
            get => _channelCount;
            set => SetField(ref _channelCount, value);
        }

        public override bool Load3rdParty(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext.StartsWith('.'))
                ext = ext[1..];
            switch (ext)
            {
                case "wav":
                    LoadWav(filePath);
                    break;
                case "ogg":
                    LoadOgg(filePath);
                    break;
                case "mp3":
                    LoadMp3(filePath);
                    break;
                case "flac":
                    LoadFlac(filePath);
                    break;
            }
            return true;
        }

        private void LoadOgg(string filePath)
        {
            using VorbisReader reader = new(filePath);
            var data = new float[reader.TotalSamples * reader.Channels];
            reader.ReadSamples(data, 0, data.Length);
            _data = DataSource.FromArray(data);
            _type = EPCMType.Float;
            _frequency = reader.SampleRate;
            _channelCount = reader.Channels;
        }

        public void LoadFlac(string filePath)
        {
            //using FlacReader reader = new(filePath);
            //_data = new byte[reader.Length];
            //reader.Read(_data, 0, _data.Length);
            //_frequency = reader.WaveFormat.SampleRate;
            //_channelCount = reader.WaveFormat.Channels;
        }

        public unsafe void LoadMp3(string filePath)
        {
            using Mp3FileReader reader = new(filePath);
            using WaveStream pcmStream = WaveFormatConversionStream.CreatePcmStream(reader);

            // Get total bytes needed for PCM data
            long totalBytes = pcmStream.Length;
            byte[] bytes = new byte[totalBytes];

            // Read chunks until we have all data
            int totalBytesRead = 0;
            int bytesRead;
            while ((bytesRead = pcmStream.Read(bytes, totalBytesRead, bytes.Length - totalBytesRead)) > 0)
                totalBytesRead += bytesRead;
            
            _data = new DataSource(bytes);

            // PCM format will always be 16-bit after conversion
            _type = EPCMType.Short;
            _frequency = pcmStream.WaveFormat.SampleRate;
            _channelCount = pcmStream.WaveFormat.Channels;
        }

        public void LoadWav(string filePath)
        {
            using WaveFileReader reader = new(filePath);
            byte[] bytes = new byte[reader.Length];
            reader.Read(bytes, 0, bytes.Length);
            switch (reader.WaveFormat.BitsPerSample)
            {
                case 8:
                    _data = new DataSource(bytes);
                    _type = EPCMType.Byte;
                    break;
                case 16:
                    _data = new DataSource(bytes);
                    _type = EPCMType.Short;
                    break;
                case 32:
                    _data = new DataSource(bytes);
                    _type = EPCMType.Float;
                    break;
                default:
                    float[] floatData = ConvertToFloat(bytes, reader.WaveFormat.BitsPerSample, false);
                    _data = DataSource.FromArray(floatData);
                    _type = EPCMType.Float;
                    break;
            }
            _frequency = reader.WaveFormat.SampleRate;
            _channelCount = reader.WaveFormat.Channels;
        }

        /// <summary>
        /// Converts stereo audio data to mono by averaging the left and right channels.
        /// If the audio data is already mono or null, this method does nothing.
        /// Stereo audio data must be mono in order to be used as spatial audio.
        /// </summary>
        public void ConvertToMono()
        {
            if (_data is null || _channelCount != 2)
                return;

            switch (_type)
            {
                case EPCMType.Byte:
                    _data = new DataSource(ConvertStereoToMono(GetByteData()));
                    break;
                case EPCMType.Short:
                    _data = DataSource.FromArray(ConvertStereoToMono(GetShortData()));
                    break;
                case EPCMType.Float:
                    _data = DataSource.FromArray(ConvertStereoToMono(GetFloatData()));
                    break;
            }
            _channelCount = 1;
        }

        public void IsolateLeftAsMono()
        {
            if (_data is null || _channelCount != 2)
                return;
            switch (_type)
            {
                case EPCMType.Byte:
                    _data = new DataSource(GetOneChannelFromStereo(GetByteData(), true));
                    break;
                case EPCMType.Short:
                    _data = DataSource.FromArray(GetOneChannelFromStereo(GetShortData(), true));
                    break;
                case EPCMType.Float:
                    _data = DataSource.FromArray(GetOneChannelFromStereo(GetFloatData(), true));
                    break;
            }
            _channelCount = 1;
        }

        public void IsolateRightAsMono()
        {
            if (_data is null || _channelCount != 2)
                return;
            switch (_type)
            {
                case EPCMType.Byte:
                    _data = new DataSource(GetOneChannelFromStereo(GetByteData(), false));
                    break;
                case EPCMType.Short:
                    _data = DataSource.FromArray(GetOneChannelFromStereo(GetShortData(), false));
                    break;
                case EPCMType.Float:
                    _data = DataSource.FromArray(GetOneChannelFromStereo(GetFloatData(), false));
                    break;
            }
            _channelCount = 1;
        }

        public AudioData AsMono()
        {
            if (Mono)
                return this;
            AudioData mono = new()
            {
                _data = _data,
                _frequency = _frequency,
                _channelCount = 1,
                _type = _type
            };
            mono.ConvertToMono();
            return mono;
        }

        public AudioData AsLeftMono()
        {
            if (Mono)
                return this;
            AudioData mono = new()
            {
                _data = _data,
                _frequency = _frequency,
                _channelCount = 1,
                _type = _type
            };
            mono.IsolateLeftAsMono();
            return mono;
        }

        public AudioData AsRightMono()
        {
            if (Mono)
                return this;
            AudioData mono = new()
            {
                _data = _data,
                _frequency = _frequency,
                _channelCount = 1,
                _type = _type
            };
            mono.IsolateRightAsMono();
            return mono;
        }

        private static byte[] ConvertStereoToMono(byte[] stereoSamples)
        {
            int monoLength = stereoSamples.Length / 2;
            byte[] monoSamples = new byte[monoLength];

            for (int i = 0, j = 0; i < monoLength; i++, j += 2)
            {
                // Since 8-bit PCM is usually unsigned, ranging from 0 to 255
                int left = stereoSamples[j];
                int right = stereoSamples[j + 1];

                // Average the two channels
                int mono = (left + right) / 2;

                monoSamples[i] = (byte)mono;
            }

            return monoSamples;
        }
        private static short[] ConvertStereoToMono(short[] stereoSamples)
        {
            int monoLength = stereoSamples.Length / 2;
            short[] monoSamples = new short[monoLength];

            for (int i = 0, j = 0; i < monoLength; i++, j += 2)
            {
                int left = stereoSamples[j];
                int right = stereoSamples[j + 1];

                // Average the two channels
                int mono = (left + right) / 2;

                monoSamples[i] = (short)mono;
            }

            return monoSamples;
        }
        private static float[] ConvertStereoToMono(float[] stereoSamples)
        {
            int monoLength = stereoSamples.Length / 2;
            float[] monoSamples = new float[monoLength];

            for (int i = 0, j = 0; i < monoLength; i++, j += 2)
            {
                float left = stereoSamples[j];
                float right = stereoSamples[j + 1];

                // Average the two channels
                float mono = (left + right) * 0.5f;

                monoSamples[i] = mono;
            }

            return monoSamples;
        }

        private static byte[] GetOneChannelFromStereo(byte[] stereoSamples, bool left)
        {
            int monoLength = stereoSamples.Length / 2;
            byte[] monoSamples = new byte[monoLength];

            for (int i = 0, j = 0; i < monoLength; i++, j += 2)
                monoSamples[i] = left ? stereoSamples[j] : stereoSamples[j + 1];
            
            return monoSamples;
        }
        private static short[] GetOneChannelFromStereo(short[] stereoSamples, bool left)
        {
            int monoLength = stereoSamples.Length / 2;
            short[] monoSamples = new short[monoLength];

            for (int i = 0, j = 0; i < monoLength; i++, j += 2)
                monoSamples[i] = left ? stereoSamples[j] : stereoSamples[j + 1];

            return monoSamples;
        }
        private static float[] GetOneChannelFromStereo(float[] stereoSamples, bool left)
        {
            int monoLength = stereoSamples.Length / 2;
            float[] monoSamples = new float[monoLength];

            for (int i = 0, j = 0; i < monoLength; i++, j += 2)
                monoSamples[i] = left ? stereoSamples[j] : stereoSamples[j + 1];

            return monoSamples;
        }

        public static float[] ConvertToFloat(byte[] buffer, int bitsPerSample, bool isBigEndian = false)
        {
            int bytesPerSample = bitsPerSample / 8;
            int sampleCount = buffer.Length / bytesPerSample;
            float[] floatSamples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                int sampleStartIndex = i * bytesPerSample;
                int sample = 0;

                // Read sample bytes and assemble into an integer
                if (isBigEndian)
                {
                    for (int byteIndex = 0; byteIndex < bytesPerSample; byteIndex++)
                    {
                        sample <<= 8;
                        sample |= buffer[sampleStartIndex + byteIndex];
                    }
                }
                else
                {
                    for (int byteIndex = bytesPerSample - 1; byteIndex >= 0; byteIndex--)
                    {
                        sample <<= 8;
                        sample |= buffer[sampleStartIndex + byteIndex];
                    }
                }

                // Sign-extend if necessary
                int signBit = 1 << (bitsPerSample - 1);
                int fullScale = signBit - 1;
                if ((sample & signBit) != 0)
                {
                    // Negative value
                    sample -= (signBit << 1);
                }

                // Normalize sample to range -1.0f to 1.0f
                floatSamples[i] = sample / (float)fullScale;

                // Special handling for unsigned 8-bit PCM (common in WAV files)
                if (bitsPerSample == 8)
                {
                    // 8-bit PCM is usually unsigned
                    floatSamples[i] = (buffer[sampleStartIndex] - 128) / 128f;
                }
            }

            return floatSamples;
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();

            _data?.Dispose();
            _data = null;
            _frequency = 44100;
            _channelCount = 1;
            _type = EPCMType.Byte;
        }

        public bool GetSamples(float[] samples, int sampleOffset)
        {
            if (_data is null)
                return false;

            switch (_type)
            {
                case EPCMType.Byte:
                    byte[] byteData = GetByteData();
                    for (int i = 0; i < byteData.Length; i++)
                        samples[sampleOffset + i] = byteData[i] / 128f - 1f;
                    break;
                case EPCMType.Short:
                    short[] shortData = GetShortData();
                    for (int i = 0; i < shortData.Length; i++)
                        samples[sampleOffset + i] = shortData[i] / 32768f;
                    break;
                case EPCMType.Float:
                    float[] floatData = GetFloatData();
                    for (int i = 0; i < floatData.Length; i++)
                        samples[sampleOffset + i] = floatData[i];
                    break;
            }
            return true;
        }

        public bool GetSamples(short[] samples, int sampleOffset)
        {
            if (_data is null)
                return false;

            switch (_type)
            {
                case EPCMType.Byte:
                    byte[] byteData = GetByteData();
                    for (int i = 0; i < byteData.Length; i++)
                        samples[sampleOffset + i] = (short)(byteData[i] * 256 - 32768);
                    break;
                case EPCMType.Short:
                    short[] shortData = GetShortData();
                    for (int i = 0; i < shortData.Length; i++)
                        samples[sampleOffset + i] = shortData[i];
                    break;
                case EPCMType.Float:
                    float[] floatData = GetFloatData();
                    for (int i = 0; i < floatData.Length; i++)
                        samples[sampleOffset + i] = (short)(floatData[i] * 32768);
                    break;
            }
            return true;
        }

        public bool GetSamples(byte[] samples, int sampleOffset)
        {
            if (_data is null)
                return false;

            switch (_type)
            {
                case EPCMType.Byte:
                    byte[] byteData = GetByteData();
                    for (int i = 0; i < byteData.Length; i++)
                        samples[sampleOffset + i] = byteData[i];
                    break;
                case EPCMType.Short:
                    short[] shortData = GetShortData();
                    for (int i = 0; i < shortData.Length; i++)
                        samples[sampleOffset + i] = (byte)(shortData[i] / 256 + 128);
                    break;
                case EPCMType.Float:
                    float[] floatData = GetFloatData();
                    for (int i = 0; i < floatData.Length; i++)
                        samples[sampleOffset + i] = (byte)((floatData[i] + 1f) * 128);
                    break;
            }
            return true;
        }
    }
}
