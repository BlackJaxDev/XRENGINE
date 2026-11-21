using System.Buffers.Binary;
using System.Text;

namespace XREngine.Data;

/// <summary>
/// Decodes a RIFF WAVE file in small parts. It supports integer PCM with 8, 16, 24, or 32 bits,
/// and IEEE float with 32 or 64 bits, also inside a <c>WAVE_FORMAT_EXTENSIBLE</c> header.
/// A seek moves the stream to the frame directly, so it is exact.
/// </summary>
public sealed class WavStreamingDecoder : IStreamingAudioDecoder
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    // The read buffer holds this many bytes. It is made once, so a read does not allocate.
    private const int ScratchBytes = 16 * 1024;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _dataStart;
    private readonly int _blockAlign;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;
    private readonly byte[] _scratch;
    private long _frame;
    private bool _disposed;

    /// <summary>Opens a file.</summary>
    /// <param name="filePath">The path of the WAV file.</param>
    public WavStreamingDecoder(string filePath)
        : this(File.OpenRead(filePath), leaveOpen: false)
    {
    }

    /// <summary>Reads from a stream.</summary>
    /// <param name="stream">A readable and seekable stream that holds a WAV file.</param>
    /// <param name="leaveOpen">True to keep the stream open when the decoder is disposed.</param>
    public WavStreamingDecoder(Stream stream, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));

        _stream = stream;
        _leaveOpen = leaveOpen;
        try
        {
            (_dataStart, long dataLength, ushort format, int channels, int sampleRate, int bits) = ReadHeader(stream);
            _isFloat = format == FormatIeeeFloat;
            bool supported = _isFloat ? bits is 32 or 64 : bits is 8 or 16 or 24 or 32;
            if (!supported || channels <= 0 || sampleRate <= 0)
                throw new InvalidDataException($"The WAV format {format} with {bits} bits and {channels} channels is not supported.");

            Channels = channels;
            SampleRate = sampleRate;
            _bytesPerSample = bits / 8;
            _blockAlign = _bytesPerSample * channels;
            TotalFrames = dataLength / _blockAlign;
            _scratch = new byte[Math.Max(_blockAlign, ScratchBytes / _blockAlign * _blockAlign)];
            _stream.Position = _dataStart;
        }
        catch
        {
            if (!leaveOpen)
                stream.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public int SampleRate { get; }

    /// <inheritdoc/>
    public int Channels { get; }

    /// <inheritdoc/>
    public long TotalFrames { get; }

    /// <inheritdoc/>
    public void SeekToFrame(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frame = Math.Clamp(frame, 0L, TotalFrames);
        _stream.Position = _dataStart + _frame * _blockAlign;
    }

    /// <inheritdoc/>
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long remaining = TotalFrames - _frame;
        int frames = (int)Math.Min(destination.Length / Channels, remaining);
        int written = 0;
        while (written < frames)
        {
            int chunkFrames = Math.Min(frames - written, _scratch.Length / _blockAlign);
            int bytes = chunkFrames * _blockAlign;
            _stream.ReadExactly(_scratch, 0, bytes);
            Convert(_scratch.AsSpan(0, bytes), destination.Slice(written * Channels, chunkFrames * Channels));
            written += chunkFrames;
        }
        _frame += written;
        return written;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_leaveOpen)
            _stream.Dispose();
    }

    private void Convert(ReadOnlySpan<byte> source, Span<float> destination)
    {
        int size = _bytesPerSample;
        for (int i = 0; i < destination.Length; i++)
        {
            ReadOnlySpan<byte> sample = source.Slice(i * size, size);
            destination[i] = (_isFloat, size) switch
            {
                (true, 4) => BinaryPrimitives.ReadSingleLittleEndian(sample),
                (true, _) => (float)BinaryPrimitives.ReadDoubleLittleEndian(sample),
                // 8-bit PCM is unsigned with a center of 128.
                (false, 1) => (sample[0] - 128) / 128f,
                (false, 2) => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                (false, 3) => ((sample[0] | (sample[1] << 8) | (sample[2] << 16)) << 8 >> 8) / 8388608f,
                _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
            };
        }
    }

    private static (long DataStart, long DataLength, ushort Format, int Channels, int SampleRate, int Bits) ReadHeader(Stream stream)
    {
        using BinaryReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
        stream.Position = 0;
        if (ReadTag(reader) != "RIFF")
            throw new InvalidDataException("The stream is not a RIFF file.");
        reader.ReadUInt32();
        if (ReadTag(reader) != "WAVE")
            throw new InvalidDataException("The RIFF file is not a WAVE file.");

        ushort format = 0;
        int channels = 0;
        int sampleRate = 0;
        int bits = 0;
        bool hasFormat = false;
        while (stream.Position + 8 <= stream.Length)
        {
            string tag = ReadTag(reader);
            long size = reader.ReadUInt32();
            long next = stream.Position + size + (size & 1);
            if (tag == "fmt ")
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format == FormatExtensible && size >= 40)
                {
                    reader.ReadUInt16();
                    reader.ReadUInt16();
                    reader.ReadUInt32();
                    // The first two bytes of the sub-format GUID hold the real format code.
                    format = reader.ReadUInt16();
                }
                hasFormat = true;
            }
            else if (tag == "data")
            {
                if (!hasFormat)
                    throw new InvalidDataException("The WAV file has a data chunk before its format chunk.");
                if (format != FormatPcm && format != FormatIeeeFloat)
                    throw new InvalidDataException($"The WAV format code {format} is not supported.");
                long length = Math.Min(size, stream.Length - stream.Position);
                return (stream.Position, length, format, channels, sampleRate, bits);
            }
            stream.Position = next;
        }
        throw new InvalidDataException("The WAV file has no data chunk.");
    }

    private static string ReadTag(BinaryReader reader)
    {
        Span<byte> tag = stackalloc byte[4];
        reader.BaseStream.ReadExactly(tag);
        return Encoding.ASCII.GetString(tag);
    }
}
