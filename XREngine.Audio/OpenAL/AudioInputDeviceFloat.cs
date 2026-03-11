using Silk.NET.OpenAL.Extensions.EXT;
using XREngine.Core;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Audio
{
    public class AudioInputDeviceFloat(
        ListenerContext listener,
        int bufferSize = 2048,
        uint freq = 22050,
        FloatBufferFormat format = FloatBufferFormat.Mono,
        string? deviceName = null) : XRBase
    {
        private uint _sampleRate = freq;
        private FloatBufferFormat _format = format;
        private int _bufferSize = bufferSize;
        private string? _deviceName = deviceName;

        public ListenerContext Listener { get; private set; } = listener;

        /// <summary>
        /// OpenAL capture wrapper. Null when the listener has no capture context
        /// (V2 non-OpenAL transports set <see cref="ListenerContext.Capture"/> to null).
        /// </summary>
        public AudioCapture<FloatBufferFormat>? AudioCapture { get; private set; }
            = listener.Capture is not null
                ? new AudioCapture<FloatBufferFormat>(listener.Capture, deviceName, freq, format, bufferSize)
                : null;

        // Pre-allocated capture buffer — DataSource.FromArray copies into native memory,
        // so reusing this across calls is safe and avoids per-capture heap allocations.
        private float[]? _floatBuffer;

        public uint SampleRate
        {
            get => _sampleRate;
            set => SetField(ref _sampleRate, value);
        }
        public FloatBufferFormat Format
        {
            get => _format;
            set => SetField(ref _format, value);
        }
        public int BufferSize
        {
            get => _bufferSize;
            set
            {
                if (SetField(ref _bufferSize, value))
                {
                    // Invalidate pre-allocated buffer so it resizes on next capture.
                    _floatBuffer = null;
                }
            }
        }
        public string? DeviceName
        {
            get => _deviceName;
            set => SetField(ref _deviceName, value);
        }

        public void StartCapture()
            => AudioCapture?.Start();

        public void StopCapture()
            => AudioCapture?.Stop();

        public unsafe bool Capture(ResourcePool<AudioData> dataPool, Queue<AudioData> buffers)
        {
            if (AudioCapture is null || Listener.Capture is null)
                return false;

            int samples = Listener.Capture.GetAvailableSamples(Listener.DeviceHandle);
            if (samples < BufferSize)
                return false;

            var buffer = dataPool.Take();

            bool stereo = Format == FloatBufferFormat.Stereo;
            _floatBuffer ??= new float[BufferSize];
            fixed (float* ptr = _floatBuffer)
            {
                Listener.Capture.CaptureSamples(Listener.DeviceHandle, ptr, BufferSize);
            }

            buffer.ChannelCount = stereo ? 2 : 1;
            buffer.Data = DataSource.FromArray(_floatBuffer);
            buffer.Frequency = (int)SampleRate;
            buffer.Type = AudioData.EPCMType.Float;

            buffers.Enqueue(buffer);
            return true;
        }
    }
}