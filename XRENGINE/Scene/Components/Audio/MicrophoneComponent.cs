using NAudio.Wave;
using System.Collections;

namespace XREngine.Components.Scene
{

    //[RequireComponents(typeof(AudioSourceComponent))]
    public class MicrophoneComponent : XRComponent
    {
        private const int DefaultBufferCapacity = 16;

        public AudioSourceComponent? GetAudioSourceComponent(bool forceCreate)
            => GetSiblingComponent<AudioSourceComponent>(forceCreate);

        private WaveInEvent? _waveIn;
        private int _deviceIndex = 0;
        private int _bufferMs = 100;
        private int _sampleRate = Engine.Audio.SampleRate;
        private int _bitsPerSample = 8;
        private bool _receive = true;
        private bool _capture = true;
        private bool _compressOverNetwork = true;

        private byte[] _currentBuffer = [];
        private int _currentBufferOffset = 0;
        private bool _muted = false;
        private float _lowerCutOff = 0.01f;
        private bool _loopback = false;

        public enum EBitsPerSample
        {
            Eight = 8,
            Sixteen = 16,
            ThirtyTwo = 32
        }

        public EBitsPerSample BitsPerSample
        {
            get => (EBitsPerSample)_bitsPerSample;
            set => _bitsPerSample = (int)value;
        }

        /// <summary>
        /// Whether to capture and broadcast audio from the local microphone.
        /// </summary>
        public bool Capture
        {
            get => _capture;
            set => SetField(ref _capture, value);
        }
        /// <summary>
        /// Whether to receive audio from the remote microphone.
        /// </summary>
        public bool Receive
        {
            get => _receive;
            set => SetField(ref _receive, value);
        }

        /// <summary>
        /// Indicates the user wants to listen to their own microphone.
        /// </summary>
        public bool Loopback
        {
            get => _loopback;
            set => SetField(ref _loopback, value);
        }

        /// <summary>
        /// Whether to compress audio data before sending it over the network.
        /// </summary>
        public bool CompressOverNetwork
        {
            get => _compressOverNetwork;
            set => SetField(ref _compressOverNetwork, value);
        }

        /// <summary>
        /// The index of the audio device to capture from.
        /// Device 0 is the default device set in Windows.
        /// </summary>
        public int DeviceIndex
        {
            get => _deviceIndex;
            set => SetField(ref _deviceIndex, value);
        }
        /// <summary>
        /// The size of the buffer in milliseconds.
        /// </summary>
        public int BufferMs
        {
            get => _bufferMs;
            set => SetField(ref _bufferMs, value);
        }
        /// <summary>
        /// The sample rate of the audio.
        /// </summary>
        public int SampleRate
        {
            get => _sampleRate;
            set => SetField(ref _sampleRate, value);
        }
        //public int Bits
        //{
        //    get => _bits;
        //    set => SetField(ref _bits, value);
        //}
        /// <summary>
        /// Whether the microphone is muted.
        /// Separate from Capture to allow for receiving audio while not broadcasting - faster than re-initializing the capture device.
        /// If enabled on the sending end, the audio will still be captured but not broadcast.
        /// If enabled on the receiving end, the audio will be received but not played.
        /// TODO: also notify the sending end to not send audio to this client if muted - wasted bandwidth.
        /// </summary>
        public bool Muted
        {
            get => _muted;
            set => SetField(ref _muted, value);
        }

        public float LowerCutOff
        {
            get => _lowerCutOff;
            set => SetField(ref _lowerCutOff, value);
        }

        public bool IsCapturing => _waveIn is not null;

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(DeviceIndex):
                case nameof(BufferMs):
                case nameof(SampleRate):
                case nameof(Capture):
                    //case nameof(Bits):
                    if (IsActiveInHierarchy)
                    {
                        if (IsCapturing && Capture)
                            StartCapture();
                        else
                            StopCapture();
                    }
                    break;
            }
        }

        public static string[] GetInputDeviceNames()
        {
            List<string> devices = [];
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                devices.Add(WaveInEvent.GetCapabilities(i).ProductName);
            return [.. devices];
        }

        public void StartCapture()
        {
            if (_waveIn is not null)
                return;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = DeviceIndex,
                WaveFormat = new WaveFormat(SampleRate, _bitsPerSample, channels: 1),
                BufferMilliseconds = BufferMs
            };

            //((Samples / 1 Second) * (Bits / 1 Sample) / 8) * (BufferMs / 1000) = bytes per second * seconds = bytes
            int bufferSize = SampleRate * _bitsPerSample / 8 * BufferMs / 1000;

            _currentBuffer = new byte[bufferSize];
            _waveIn.DataAvailable += WaveIn_DataAvailable;
            _waveIn.StartRecording();

            //InputDevice.StartCapture();
            //RegisterTick(ETickGroup.Normal, ETickOrder.Input, CaptureSamples);
        }
        public void StopCapture()
        {
            if (_waveIn is null)
                return;

            _waveIn.DataAvailable -= WaveIn_DataAvailable;
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;

            //InputDevice.StopCapture();
        }

        private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (Muted)
                return;

            int remainingByteCount = e.BytesRecorded;
            int srcOffset = 0;
            while (remainingByteCount > 0)
            {
                int endIndex = _currentBufferOffset + remainingByteCount;
                if (endIndex <= _currentBuffer.Length)
                {
                    //If the buffer has enough space, just copy the data and move on with our life
                    Buffer.BlockCopy(e.Buffer, srcOffset, _currentBuffer, _currentBufferOffset, remainingByteCount);

                    srcOffset += remainingByteCount;
                    _currentBufferOffset += remainingByteCount;
                    remainingByteCount = 0;

                    //If the buffer happens to be perfectly filled (edge case), queue it
                    if (_currentBufferOffset == _currentBuffer.Length)
                    {
                        ReplicateCurrentBuffer();
                        _currentBufferOffset = 0;
                        //Array.Fill<byte>(_currentBuffer, 0);
                    }
                }
                else
                {
                    //Consume remaining space from the available data
                    int remainingSpace = _currentBuffer.Length - _currentBufferOffset;
                    if (remainingSpace > 0)
                        Buffer.BlockCopy(e.Buffer, srcOffset, _currentBuffer, _currentBufferOffset, remainingSpace);

                    srcOffset += remainingSpace;
                    remainingByteCount -= remainingSpace;

                    ReplicateCurrentBuffer();
                    _currentBufferOffset = 0;
                    //Array.Fill<byte>(_currentBuffer, 0);
                }
            }
        }

        private bool VerifyLowerCutoff()
        {
            // If cutoff is nearly zero, then there's no need to check the samples.
            if (_lowerCutOff < float.Epsilon)
                return true;

            float sumSquares = 0.0f;

            if (_bitsPerSample == 8)
            {
                // For 8-bit PCM, samples are unsigned bytes (0-255). The midpoint is 128.
                // We'll normalize each sample to [-1, 1] then compute the RMS.
                for (int i = 0; i < _currentBuffer.Length; i++)
                {
                    float normalized = (_currentBuffer[i] - 128) / 127.0f;
                    sumSquares += normalized * normalized;
                }
            }
            else if (_bitsPerSample == 16)
            {
                // For 16-bit PCM, samples are signed shorts (-32768 to 32767).
                // We'll normalize each sample to [-1, 1] then compute the RMS.
                for (int i = 0; i < _currentBuffer.Length; i += 2)
                {
                    short sample = BitConverter.ToInt16(_currentBuffer, i);
                    float normalized = sample / 32768.0f;
                    sumSquares += normalized * normalized;
                }
            }
            else if (_bitsPerSample == 32)
            {
                // For 32-bit PCM, samples are floats (-1 to 1).
                // We'll compute the RMS directly.
                for (int i = 0; i < _currentBuffer.Length; i += 4)
                {
                    float sample = BitConverter.ToSingle(_currentBuffer, i);
                    sumSquares += sample * sample;
                }
            }
            else
            {
                throw new NotSupportedException($"Unsupported bits per sample: {_bitsPerSample}");
            }

            float rmsSq = sumSquares / (_currentBuffer.Length / (_bitsPerSample / 8));
            //Debug.Out($"Mic RMS: {Math.Sqrt(rmsSq)}");
            return rmsSq >= _lowerCutOff * _lowerCutOff;
        }

        private void ReplicateCurrentBuffer()
        {
            //Denoise(ref _currentBuffer);

            if (!VerifyLowerCutoff())
                return;

            if (Loopback)
                ReceiveData(nameof(_currentBuffer), _currentBuffer.ToArray());
            else
                EnqueueDataReplication(nameof(_currentBuffer), _currentBuffer.ToArray(), CompressOverNetwork, false);
        }

        private float _smoothingFactor = 1.0f; // Default value of 0.5 (50% smoothing)
        /// <summary>
        /// Controls the strength of the noise reduction filter. 
        /// Range is 0.0 to 1.0, where 0.0 is no smoothing and 1.0 is maximum smoothing.
        /// Default is 0.5 (50% smoothing).
        /// </summary>
        public float SmoothingFactor
        {
            get => _smoothingFactor;
            set => SetField(ref _smoothingFactor, Math.Clamp(value, 0f, 1f));
        }

        private void Denoise(ref byte[] currentBuffer)
        {
            int length = currentBuffer.Length;
            if (length < 3 || _smoothingFactor < float.Epsilon)
                return;

            // Handle different bit depths
            switch (_bitsPerSample)
            {
                case 8:
                    byte[] smoothedBytes = new byte[length];

                    // Process first sample
                    smoothedBytes[0] = (byte)((
                        currentBuffer[0] * (1 - _smoothingFactor) +
                        currentBuffer[1] * _smoothingFactor));

                    // Process middle samples
                    for (int i = 1; i < length - 1; i++)
                    {
                        float sum =
                            currentBuffer[i - 1] * _smoothingFactor / 2 +
                            currentBuffer[i] * (1 - _smoothingFactor) +
                            currentBuffer[i + 1] * _smoothingFactor / 2;
                        smoothedBytes[i] = (byte)sum;
                    }

                    // Process last sample
                    smoothedBytes[length - 1] = (byte)((
                        currentBuffer[length - 2] * _smoothingFactor +
                        currentBuffer[length - 1] * (1 - _smoothingFactor)));

                    Buffer.BlockCopy(smoothedBytes, 0, currentBuffer, 0, length);

                    break;

                case 16:
                    int shortLength = length / 2;
                    short[] samples = new short[shortLength];
                    short[] smoothedShorts = new short[shortLength];

                    Buffer.BlockCopy(currentBuffer, 0, samples, 0, length);

                    // Process first sample
                    smoothedShorts[0] = (short)((
                        samples[0] * (1 - _smoothingFactor) +
                        samples[1] * _smoothingFactor));

                    // Process middle samples
                    for (int i = 1; i < shortLength - 1; i++)
                    {
                        float sum = 
                            samples[i - 1] * _smoothingFactor / 2 +
                            samples[i] * (1 - _smoothingFactor) +
                            samples[i + 1] * _smoothingFactor / 2;
                        smoothedShorts[i] = (short)sum;
                    }

                    // Process last sample
                    smoothedShorts[shortLength - 1] = (short)((
                        samples[shortLength - 2] * _smoothingFactor +
                        samples[shortLength - 1] * (1 - _smoothingFactor)));

                    Buffer.BlockCopy(smoothedShorts, 0, currentBuffer, 0, length);

                    break;

                case 32:
                    int floatLength = length / 4;
                    float[] floatSamples = new float[floatLength];
                    float[] smoothedFloats = new float[floatLength];

                    Buffer.BlockCopy(currentBuffer, 0, floatSamples, 0, length);

                    // Process first sample
                    smoothedFloats[0] = 
                        floatSamples[0] * (1 - _smoothingFactor) +
                        floatSamples[1] * _smoothingFactor;

                    // Process middle samples
                    for (int i = 1; i < floatLength - 1; i++)
                    {
                        smoothedFloats[i] =
                            floatSamples[i - 1] * _smoothingFactor / 2 +
                            floatSamples[i] * (1 - _smoothingFactor) +
                            floatSamples[i + 1] * _smoothingFactor / 2;
                    }

                    // Process last sample
                    smoothedFloats[floatLength - 1] = 
                        floatSamples[floatLength - 2] * _smoothingFactor +
                        floatSamples[floatLength - 1] * (1 - _smoothingFactor);

                    Buffer.BlockCopy(smoothedFloats, 0, currentBuffer, 0, length);

                    break;

                default:
                    throw new NotSupportedException($"Unsupported bits per sample: {_bitsPerSample}");
            }
        }

        public override void ReceiveData(string id, object? data)
        {
            switch (id)
            {
                case nameof(_currentBuffer):
                    if (Receive && !Muted && data is ICollection col)
                        EnqueueStreamingAudio(col);
                    break;
            }
        }

        private void EnqueueStreamingAudio(ICollection col)
        {
            var buffer = new byte[col.Count];
            int i = 0;
            foreach (object b in col)
            {
                if (b is byte bVal)
                    buffer[i] = bVal;
                else if (byte.TryParse(b?.ToString(), out byte sVal))
                    buffer[i] = sVal;
                i++;
            }

            BufferReceived?.Invoke(buffer);

            var audioSource = GetAudioSourceComponent(true)!;
            audioSource.Loop = false;
            switch (_bitsPerSample)
            {
                case 8:
                    {
                        audioSource.EnqueueStreamingBuffers(SampleRate, false, buffer);
                    }
                    break;
                case 16:
                    {
                        //cast every 2 bytes to a short
                        short[] shorts = new short[buffer.Length / 2];
                        Buffer.BlockCopy(buffer, 0, shorts, 0, buffer.Length);
                        audioSource.EnqueueStreamingBuffers(SampleRate, false, shorts);
                    }
                    break;
                case 32:
                    {
                        //cast every 4 bytes to a float
                        float[] floats = new float[buffer.Length / 4];
                        Buffer.BlockCopy(buffer, 0, floats, 0, buffer.Length);
                        audioSource.EnqueueStreamingBuffers(SampleRate, false, floats);
                    }
                    break;
                default:
                    Debug.Out($"Unsupported bits per sample: {_bitsPerSample}");
                    return;
            }

            //if (audioSource.State != Audio.AudioSource.ESourceState.Playing)
            //    audioSource.Play();
        }

        public event Action<byte[]>? BufferReceived;

        //public byte[] GetByteBuffer()
        //{
        //    var buffer = GetBuffer();
        //    if (buffer is null)
        //        return [];

        //    byte[] data = buffer.GetByteData();
        //    _bufferPool.Release(buffer);
        //    return data;
        //}

        //public short[] GetShortBuffer()
        //{
        //    var buffer = GetBuffer();
        //    if (buffer is null)
        //        return [];

        //    return buffer.GetShortData();
        //}

        //public float[] GetFloatData()
        //{
        //    var buffer = GetBuffer();
        //    if (buffer is null)
        //        return [];

        //    float[] data = buffer.GetFloatData();
        //    _bufferPool.Release(buffer);
        //    return data;
        //}

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            string[] devices = GetInputDeviceNames();
            if (devices.Length == 0)
            {
                Debug.Out("No audio input devices found.");
                return;
            }
            else
                Debug.Out($"Available audio input devices:{Environment.NewLine}{string.Join(Environment.NewLine, devices)}");

            var asioNames = AsioOut.GetDriverNames();
            if (asioNames.Length > 0)
                Debug.Out($"Available ASIO devices:{Environment.NewLine}{string.Join(Environment.NewLine, asioNames)}");

            DeviceIndex = Math.Clamp(DeviceIndex, 0, devices.Length - 1);

            if (Capture)
                StartCapture();
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            StopCapture();
        }

        //I'm not using OpenAL's capture because I don't know how to select a specific device with it.

        //public void SetAudioDevice(int deviceNumber)
        //{
        //    //InputDevice = new AudioInputDevice(ListenerContext.Default, 2048, 22050, BufferFormat.Mono16, GetAudioDevices().ElementAt(deviceNumber).FriendlyName);
        //}

        //public AudioInputDevice InputDevice { get; set; }
        //public MicrophoneComponent()
        //{

        //    //InputDevice = new AudioInputDevice(ListenerContext.Default, 2048, 22050, BufferFormat.Mono16);
        //}

        //public void StartCapture()
        //{
        //    InputDevice.StartCapture();
        //}

        //public void CaptureSamples()
        //{
        //    //InputDevice.Capture(_bufferPool, BufferQueue);
        //}
    }
}
