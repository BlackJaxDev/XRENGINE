using NVorbis;

namespace XREngine.Data;

/// <summary>
/// Decodes an Ogg Vorbis file in small parts with NVorbis. A seek is exact, because the decoder never uses the NVorbis seek.
/// The NVorbis 0.10.5 seek lands a half block away from the target on most pages, throws on valid targets, and can loop forever.
/// A forward seek decodes and drops the frames up to the target, so it costs the decode time of the skipped frames.
/// </summary>
/// <remarks>
/// <para>
/// A backward seek uses an Ogg page index (<see cref="VorbisPageIndex"/>). The first backward seek builds it: it reads the
/// file once and keeps at most <see cref="VorbisPageIndex.MaxEntries"/> restart pages. A backward seek then opens NVorbis on
/// a stream that holds the stream headers and the file from the last restart page before the target, and it decodes and
/// drops the frames from that page to the target. The first packet of that decode is the pre-roll for the overlap, so the
/// result is the same, sample for sample, as a decode from frame zero. The cost is the decode of about one to two index
/// strides of pages, not of the file up to the target.
/// </para>
/// <para>
/// If the index cannot be built, or a restart from it fails or ends early, the decoder sets
/// <see cref="SeekIndexFailure"/> one time, stops using the index, and decodes from the start of the file, as it did with
/// no index. It never returns samples from a restart that it could not check.
/// </para>
/// <para>
/// The decoder itself does not allocate after the open, except one time for the index. NVorbis allocates on the heap for
/// each decoded packet (about 10 bytes for each frame of stereo audio), so a read and a seek allocate in proportion to the
/// frames that they decode.
/// </para>
/// </remarks>
public sealed class VorbisStreamingDecoder : IStreamingAudioDecoder
{
    // The discard buffer holds this many frames. It is made once, so a seek does not allocate.
    private const int DiscardFrames = 4096;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly float[] _discard;
    private readonly OggSpliceStream _splice = new();
    private VorbisReader _reader;
    private VorbisPageIndex? _index;
    private bool _readerFromIndex;
    private long _frame;
    private bool _disposed;

    /// <summary>Opens a file.</summary>
    /// <param name="filePath">The path of the Ogg Vorbis file.</param>
    public VorbisStreamingDecoder(string filePath)
        : this(File.OpenRead(filePath), leaveOpen: false)
    {
    }

    /// <summary>Reads from a stream.</summary>
    /// <param name="stream">A readable and seekable stream that holds an Ogg Vorbis file. A backward seek moves it.</param>
    /// <param name="leaveOpen">True to keep the stream open when the decoder is disposed.</param>
    public VorbisStreamingDecoder(Stream stream, bool leaveOpen)
        : this(stream, leaveOpen, useSeekIndex: true)
    {
    }

    /// <summary>Reads from a stream, with or without the Ogg page index.</summary>
    /// <param name="stream">A readable and seekable stream that holds an Ogg Vorbis file. A backward seek moves it.</param>
    /// <param name="leaveOpen">True to keep the stream open when the decoder is disposed.</param>
    /// <param name="useSeekIndex">False to decode from the start of the file at each backward seek.</param>
    internal VorbisStreamingDecoder(Stream stream, bool leaveOpen, bool useSeekIndex)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
        SeekIndexState = useSeekIndex ? EVorbisSeekIndexState.NotBuilt : EVorbisSeekIndexState.Disabled;
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

    /// <summary>The frame that the next <see cref="Read"/> starts at.</summary>
    public long FramePosition => _frame;

    /// <summary>
    /// The number of frames that NVorbis decoded since the open, including the frames that a seek dropped.
    /// The difference across a seek is the decode cost of that seek.
    /// </summary>
    public long DecodedFrames { get; private set; }

    /// <summary>The state of the Ogg page index.</summary>
    public EVorbisSeekIndexState SeekIndexState { get; private set; }

    /// <summary>
    /// The reason that the index or a restart from it failed, or null. It is set one time; the decoder then decodes from
    /// the start of the file at each backward seek. Read it only while no other thread uses the decoder.
    /// </summary>
    public string? SeekIndexFailure { get; private set; }

    /// <summary>The number of backward seeks that started at an indexed page.</summary>
    internal int IndexedSeekCount { get; private set; }

    /// <summary>The index, or null before the build and after a failure.</summary>
    internal VorbisPageIndex? PageIndex => _index;

    /// <inheritdoc/>
    /// <remarks>A backward seek starts at the nearest indexed page before the target. See the type summary for the cost.</remarks>
    public void SeekToFrame(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long target = Math.Clamp(frame, 0L, TotalFrames);
        if (target < _frame)
        {
            _reader.Dispose();
            if (!TryOpenFromIndex(target))
                OpenFromStart();
        }

        if (!_readerFromIndex)
        {
            DecodeTo(target);
            return;
        }

        bool reached;
        try
        {
            reached = DecodeTo(target);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailIndex($"The decode from the indexed page failed: {ex.Message}");
            reached = false;
        }

        if (reached)
            return;

        FailIndex($"The decode from the indexed page ended at frame {_frame}, before the target frame {target}.");
        _reader.Dispose();
        OpenFromStart();
        DecodeTo(target);
    }

    /// <inheritdoc/>
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Span<float> frames = destination[..(destination.Length / Channels * Channels)];
        if (!_readerFromIndex)
            return ReadFrames(frames);

        int read;
        try
        {
            read = ReadFrames(frames);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailIndex($"The decode from the indexed page failed: {ex.Message}");
            read = 0;
        }

        if (read > 0 || _frame >= TotalFrames || frames.IsEmpty)
            return read;

        // The decode from the indexed page ended before the end of the file. Decode again from the start to this frame.
        FailIndex($"The decode from the indexed page ended at frame {_frame}, before the end of the file at frame {TotalFrames}.");
        long frame = _frame;
        _reader.Dispose();
        OpenFromStart();
        DecodeTo(frame);
        return ReadFrames(frames);
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

    private void OpenFromStart()
    {
        _reader = OpenReader();
        _frame = 0;
        _readerFromIndex = false;
    }

    // Opens the reader at the last indexed page before the target. The old reader is disposed already.
    private bool TryOpenFromIndex(long target)
    {
        if (SeekIndexState == EVorbisSeekIndexState.NotBuilt)
            BuildIndex();
        if (_index is null || !_index.TryFind(target, out VorbisPageIndexEntry entry))
            return false;

        VorbisReader? reader = null;
        try
        {
            if (!_index.TryPrepareRestart(_stream, entry, _splice, out string? failure))
            {
                FailIndex($"The indexed page does not agree with the file: {failure}");
                return false;
            }

            reader = new VorbisReader(_splice, closeOnDispose: false);
            int channels = reader.Channels;
            int sampleRate = reader.SampleRate;
            if (channels != Channels || sampleRate != SampleRate)
            {
                reader.Dispose();
                FailIndex($"The stream from the indexed page has {channels} channels at {sampleRate} Hz, not {Channels} channels at {SampleRate} Hz.");
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reader?.Dispose();
            FailIndex($"The stream from the indexed page did not open: {ex.Message}");
            return false;
        }

        _reader = reader;
        _frame = entry.RestartFrame;
        _readerFromIndex = true;
        IndexedSeekCount++;
        return true;
    }

    private void BuildIndex()
    {
        VorbisPageIndex? index = VorbisPageIndex.TryBuild(_stream, out string? failure);
        if (index is null)
        {
            FailIndex($"The Ogg page index was not built: {failure}");
            return;
        }

        if (index.FinalGranule != TotalFrames)
        {
            FailIndex($"The Ogg page index ends at frame {index.FinalGranule}, but the decoder reports {TotalFrames} frames.");
            return;
        }

        _index = index;
        SeekIndexState = EVorbisSeekIndexState.Ready;
    }

    private void FailIndex(string reason)
    {
        _index = null;
        if (SeekIndexState == EVorbisSeekIndexState.Failed)
            return;
        SeekIndexState = EVorbisSeekIndexState.Failed;
        SeekIndexFailure = reason;
    }

    // Decodes and drops frames up to the target. Returns false if the file ended first.
    private bool DecodeTo(long target)
    {
        while (_frame < target)
        {
            int frames = (int)Math.Min(DiscardFrames, target - _frame);
            int read = ReadFrames(_discard.AsSpan(0, frames * Channels));
            if (read <= 0)
                return false;
        }
        return true;
    }

    // NVorbis can return fewer samples than asked at a packet boundary, so read until the span is full or the file ends.
    // The read stops at the frame count of the file.
    private int ReadFrames(Span<float> destination)
    {
        long left = Math.Max(0L, TotalFrames - _frame);
        if (destination.Length / Channels > left)
            destination = destination[..(int)(left * Channels)];

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
        DecodedFrames += frames;
        return frames;
    }
}
