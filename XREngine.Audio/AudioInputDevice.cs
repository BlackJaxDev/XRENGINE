using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;
using XREngine.Core;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Audio
{
    public class AudioInputDevice(
        ListenerContext listener,
        int bufferSize = 2048,
        uint freq = 22050,
        BufferFormat format = BufferFormat.Mono16,
        string? deviceName = null) : XRBase
    {
        private uint _sampleRate = freq;
        private BufferFormat _format = format;
        private int _bufferSize = bufferSize;
        private string? _deviceName = deviceName;

        public ListenerContext Listener { get; private set; } = listener;

        /// <summary>
        /// OpenAL capture wrapper. Null when the listener has no capture context
        /// (V2 non-OpenAL transports set <see cref="ListenerContext.Capture"/> to null).
        /// </summary>
        public AudioCapture<BufferFormat>? AudioCapture { get; private set; }
            = listener.Capture is not null
                ? new AudioCapture<BufferFormat>(listener.Capture, deviceName, freq, format, bufferSize)
                : null;

        // Pre-allocated capture buffers — DataSource.FromArray copies into native memory,
        // so reusing these across calls is safe and avoids per-capture heap allocations.
        private byte[]? _byteBuffer;
        private short[]? _shortBuffer;

        public uint SampleRate
        {
            get => _sampleRate;
            set => SetField(ref _sampleRate, value);
        }
        public BufferFormat Format
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
                    // Invalidate pre-allocated buffers so they resize on next capture.
                    _byteBuffer = null;
                    _shortBuffer = null;
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

            switch (Format)
            {
                case BufferFormat.Mono8:
                case BufferFormat.Stereo8:
                    {
                        bool stereo = Format == BufferFormat.Stereo8;
                        _byteBuffer ??= new byte[BufferSize];
                        fixed (byte* ptr = _byteBuffer)
                        {
                            Listener.Capture.CaptureSamples(Listener.DeviceHandle, ptr, BufferSize);
                        }
                        buffer.ChannelCount = stereo ? 2 : 1;
                        buffer.Data = DataSource.FromArray(_byteBuffer);
                        buffer.Frequency = (int)SampleRate;
                        buffer.Type = AudioData.EPCMType.Byte;
                    }
                    break;
                case BufferFormat.Mono16:
                case BufferFormat.Stereo16:
                    {
                        bool stereo = Format == BufferFormat.Stereo16;
                        _shortBuffer ??= new short[BufferSize];
                        fixed (short* ptr = _shortBuffer)
                        {
                            Listener.Capture.CaptureSamples(Listener.DeviceHandle, ptr, BufferSize);
                        }
                        buffer.ChannelCount = stereo ? 2 : 1;
                        buffer.Data = DataSource.FromArray(_shortBuffer);
                        buffer.Frequency = (int)SampleRate;
                        buffer.Type = AudioData.EPCMType.Short;
                    }
                    break;
                default:
                    return false;
            }
            buffers.Enqueue(buffer);
            return true;
        }
    }
}