using NVorbis;

namespace XREngine.Data;

/// <summary>
/// Decodes an Ogg Vorbis file in small parts with NVorbis. A seek is exact, because the decoder never uses the NVorbis seek.
/// The NVorbis 0.10.5 seek lands a half block away from the target on most pages, throws on valid targets, and can loop forever.
/// A forward seek decodes and drops the frames up to the target, so it costs the decode time of the skipped frames.
/// A backward seek opens the stream again at its start and decodes and drops the frames up to the target, so its cost is
/// O(n) in the target frame: a jump near the end of a long file decodes almost the whole file.
/// </summary>
public sealed class VorbisStreamingDecoder : IStreamingAudioDecoder
{
    // The discard buffer holds this many frames. It is made once, so a seek does not allocate.
    private const int DiscardFrames = 4096;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly float[] _discard;
    private VorbisReader _reader;
    private long _frame;
    private bool _disposed;

    /// <summary>Opens a file.</summary>
    /// <param name="filePath">The path of the Ogg Vorbis file.</param>
    public VorbisStreamingDecoder(string filePath)
        : this(File.OpenRead(filePath), leaveOpen: false)
    {
    }

    /// <summary>Reads from a stream.</summary>
    /// <param name="stream">A readable and seekable stream that holds an Ogg Vorbis file. A backward seek moves it to its start.</param>
    /// <param name="leaveOpen">True to keep the stream open when the decoder is disposed.</param>
    public VorbisStreamingDecoder(Stream stream, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
        try
        {
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));

            _reader = OpenReader();
            if (_reader.Channels <= 0 || _reader.SampleRate <= 0)
                throw new InvalidDataException("The Ogg Vorbis stream has no valid channel count or sample rate.");

            Channels = _reader.Channels;
            SampleRate = _reader.SampleRate;
            TotalFrames = _reader.TotalSamples;

            // The length query can read pages, so a new reader starts the decode at a known state.
            _reader.Dispose();
            _reader = OpenReader();
            _discard = new float[DiscardFrames * Channels];
        }
        catch
        {
            _reader?.Dispose();
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
    /// <remarks>A backward seek decodes the file again from the start. See the type summary for the cost.</remarks>
    public void SeekToFrame(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long target = Math.Clamp(frame, 0L, TotalFrames);
        if (target < _frame)
        {
            _reader.Dispose();
            _reader = OpenReader();
            _frame = 0;
        }

        while (_frame < target)
        {
            int frames = (int)Math.Min(DiscardFrames, target - _frame);
            int read = ReadFrames(_discard.AsSpan(0, frames * Channels));
            if (read <= 0)
                break;
        }
    }

    /// <inheritdoc/>
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadFrames(destination[..(destination.Length / Channels * Channels)]);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _reader.Dispose();
        if (!_leaveOpen)
            _stream.Dispose();
    }

    private VorbisReader OpenReader()
    {
        _stream.Position = 0;
        return new VorbisReader(_stream, closeOnDispose: false);
    }

    // NVorbis can return fewer samples than asked at a packet boundary, so read until the span is full or the file ends.
    private int ReadFrames(Span<float> destination)
    {
        int written = 0;
        while (written < destination.Length)
        {
            int count = _reader.ReadSamples(destination[written..]);
            if (count <= 0)
                break;
            written += count;
        }
        int frames = written / Channels;
        _frame += frames;
        return frames;
    }
}
