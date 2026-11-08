using NVorbis;

namespace XREngine.Data;

/// <summary>
/// Decodes an Ogg Vorbis file in small parts with NVorbis. A seek is exact, because the decoder never uses the NVorbis seek.
/// The NVorbis 0.10.5 seek lands a half block away from the target on most pages, throws on valid targets, and can loop forever.
/// A forward seek decodes and drops the frames up to the target, so it costs the decode time of the skipped frames.
/// </summary>
/// <remarks>
/// <para>
/// A backward seek uses an Ogg page index (<see cref="VorbisPageIndex"/>). The open starts the build of the index on a job
/// thread, on a second read-only stream of the same file, so the thread that uses the decoder never builds it. The index
/// keeps at most <see cref="VorbisPageIndex.MaxEntries"/> restart pages. A backward seek opens NVorbis on a stream that
/// holds the stream headers and the file from the last restart page before the target, and it decodes and drops the
/// frames from that page to the target. The first packet of that decode is the pre-roll for the overlap, so the result is
/// the same, sample for sample, as a decode from frame zero. The cost is the decode of about one to two index strides of
/// pages, not of the file up to the target.
/// </para>
/// <para>
/// A seek before the index is ready, and a seek to the first indexed page or earlier, decodes from the start of the file
/// and does not wait for the index. A stream that cannot be opened a second time has no index
/// (<see cref="EVorbisSeekIndexState.Disabled"/>).
/// </para>
/// <para>
/// If the index cannot be built, or a restart from it fails or ends early (in a seek or in a read), the decoder sets
/// <see cref="SeekIndexFailure"/> one time, stops using the index, and decodes from the start of the file, as it did with
/// no index. It never returns samples from a restart that it could not check.
/// </para>
/// <para>
/// The decoder itself does not allocate after the open; the index allocates one time on its job thread. NVorbis allocates
/// on the heap for each decoded packet (about 10 bytes for each frame of stereo audio), so a read and a seek allocate in
/// proportion to the frames that they decode.
/// </para>
/// </remarks>
public sealed class VorbisStreamingDecoder : IStreamingAudioDecoder
{
    // The discard buffer holds this many frames. It is made once, so a seek does not allocate.
    private const int DiscardFrames = 4096;

    // Index builds run one at a time, so the open of a sequence with many clips does not fill the disk queue.
    private static readonly TaskScheduler DefaultIndexScheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1).ConcurrentScheduler;

    private static readonly Action<object?> RunIndexBuildAction = static state => ((VorbisStreamingDecoder)state!).RunIndexBuild();

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly float[] _discard;
    private readonly OggSpliceStream _splice = new();
    private VorbisReader _reader;
    private VorbisPageIndex? _index;
    private readonly Func<Stream>? _openIndexStream;
    private readonly CancellationTokenSource? _indexCancel;
    private readonly Task? _indexBuild;

    // The build task writes the result and then sets the done flag with a volatile write. The decoder thread takes it.
    private VorbisPageIndex? _builtIndex;
    private string? _buildFailure;
    private int _buildDone;
    private int _indexBuildThreadId;
    private bool _readerFromIndex;
    private long _frame;
    private bool _disposed;

    /// <summary>Opens a file. The page index builds on a job thread from a second read-only stream of the file.</summary>
    /// <param name="filePath">The path of the Ogg Vorbis file.</param>
    public VorbisStreamingDecoder(string filePath)
        : this(filePath, buildSeekIndex: true)
    {
    }

    /// <summary>Opens a file, with or without the page index.</summary>
    /// <param name="filePath">The path of the Ogg Vorbis file.</param>
    /// <param name="buildSeekIndex">
    /// False for a decoder that only decodes forward (for example the proxy bake). It starts no index build, and a backward
    /// seek decodes from the start of the file (<see cref="EVorbisSeekIndexState.Disabled"/>, with no diagnostic).
    /// </param>
    public VorbisStreamingDecoder(string filePath, bool buildSeekIndex)
        : this(File.OpenRead(filePath), leaveOpen: false, buildSeekIndex ? OpenFileForIndex(filePath) : null, null, null)
    {
    }

    /// <summary>
    /// Reads from a stream. The page index builds on a job thread from a second stream: the same file for a
    /// <see cref="FileStream"/>, or the same buffer for a <see cref="MemoryStream"/> with a visible buffer. Any other stream
    /// cannot be opened a second time, so it has no index: <see cref="SeekIndexState"/> is <see cref="EVorbisSeekIndexState.Failed"/>
    /// and <see cref="SeekIndexFailure"/> gives the reason, so the audio track reports it one time.
    /// </summary>
    /// <param name="stream">A readable and seekable stream that holds an Ogg Vorbis file. A backward seek moves it.</param>
    /// <param name="leaveOpen">True to keep the stream open when the decoder is disposed.</param>
    public VorbisStreamingDecoder(Stream stream, bool leaveOpen)
        : this(stream, leaveOpen, GetIndexStreamSource(stream), null, NoSecondStreamReason)
    {
    }

    /// <summary>Reads from a stream, and builds the page index on a job thread from a second stream of the same data.</summary>
    /// <param name="stream">A readable and seekable stream that holds an Ogg Vorbis file. A backward seek moves it.</param>
    /// <param name="leaveOpen">True to keep the stream open when the decoder is disposed.</param>
    /// <param name="openIndexStream">
    /// Opens a new readable and seekable stream with the same data. The build calls it one time on a job thread and
    /// disposes the stream. Null gives no index, with no diagnostic, for a decoder that only decodes forward.
    /// </param>
    public VorbisStreamingDecoder(Stream stream, bool leaveOpen, Func<Stream>? openIndexStream)
        : this(stream, leaveOpen, openIndexStream, null, null)
    {
    }

    /// <summary>Reads from a stream, with the scheduler of the index build. A test gives a scheduler that it runs by hand.</summary>
    /// <param name="stream">A readable and seekable stream that holds an Ogg Vorbis file. A backward seek moves it.</param>
    /// <param name="leaveOpen">True to keep the stream open when the decoder is disposed.</param>
    /// <param name="openIndexStream">Opens a second stream of the same data for the index build, or null for no index.</param>
    /// <param name="indexScheduler">The scheduler of the index build, or null for the shared one-at-a-time scheduler.</param>
    /// <param name="noIndexReason">
    /// The reason to report when <paramref name="openIndexStream"/> is null, or null for a decoder that chose to have no index.
    /// </param>
    internal VorbisStreamingDecoder(Stream stream, bool leaveOpen, Func<Stream>? openIndexStream, TaskScheduler? indexScheduler, string? noIndexReason = null)
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
            // NVorbis drops the start of a decode when its first packet is long and the next packet short. The frame-zero
            // decode is the timeline, so the frame count is the last granule position minus that dropped start.
            long totalSamples = _reader.TotalSamples;
            _stream.Position = 0;
            StartDrop = VorbisPageIndex.ReadStartDrop(_stream);
            TotalFrames = Math.Max(0L, totalSamples - StartDrop);

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

        if (openIndexStream is null)
        {
            SeekIndexState = EVorbisSeekIndexState.Disabled;
            if (noIndexReason is not null)
                FailIndex(noIndexReason);
            return;
        }

        SeekIndexState = EVorbisSeekIndexState.Building;
        _openIndexStream = openIndexStream;
        _indexCancel = new CancellationTokenSource();

        // A build that still waits in the queue when the decoder closes never starts.
        _indexBuild = Task.Factory.StartNew(RunIndexBuildAction, this, _indexCancel.Token, TaskCreationOptions.DenyChildAttach, indexScheduler ?? DefaultIndexScheduler);
    }

    /// <inheritdoc/>
    public int SampleRate { get; }

    /// <inheritdoc/>
    public int Channels { get; }

    /// <inheritdoc/>
    public long TotalFrames { get; }

    /// <summary>
    /// The frames at the start of the granule timeline that the frame-zero decode does not give: a quarter of the long
    /// block minus a quarter of the short block when the first audio packet is long and the second short, else zero.
    /// Frame zero of the decoder is granule frame <see cref="StartDrop"/>.
    /// </summary>
    internal int StartDrop { get; }

    /// <summary>True if the index build task ran its work. A cancelled build that never started is false.</summary>
    internal bool IndexBuildStarted => IndexBuildThreadId != 0;

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

    /// <summary>The index, or null before the decoder took the build result and after a failure.</summary>
    internal VorbisPageIndex? PageIndex => _index;

    /// <summary>The managed thread ID of the thread that ran the index build, or zero before the build starts.</summary>
    internal int IndexBuildThreadId => Volatile.Read(ref _indexBuildThreadId);

    /// <summary>Waits for the index build to end and takes its result. A test uses it to get a known index state.</summary>
    /// <param name="timeout">The longest wait.</param>
    /// <returns>True if the build ended in time, or if there is no build.</returns>
    internal bool WaitForSeekIndex(TimeSpan timeout)
    {
        if (_indexBuild is null)
            return true;
        bool ended = _indexBuild.Wait(timeout);
        AdoptIndexBuild();
        return ended;
    }

    /// <inheritdoc/>
    /// <remarks>A backward seek starts at the nearest indexed page before the target. See the type summary for the cost.</remarks>
    public void SeekToFrame(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AdoptIndexBuild();
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
        AdoptIndexBuild();
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

        // A short read happens only at the end of the data, so a short read before TotalFrames is an early end.
        if (read == frames.Length / Channels || _frame >= TotalFrames)
            return read;

        // The decode from the indexed page ended before the end of the file. Decode again from the start to this frame.
        FailIndex($"The decode from the indexed page ended at frame {_frame}, before the end of the file at frame {TotalFrames}.");
        long frame = _frame;
        _reader.Dispose();
        OpenFromStart();
        DecodeTo(frame);
        return read + ReadFrames(frames[(read * Channels)..]);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _indexCancel?.Cancel();
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
        // A target at the first indexed page or earlier is near the start, so the decode from the start is short.
        if (_index is null || !_index.TryFind(target, out VorbisPageIndexEntry entry, out int entryIndex) || entryIndex == 0)
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

    // Runs on the job thread. It uses only its own stream and fields that do not change after the open.
    private void RunIndexBuild()
    {
        Volatile.Write(ref _indexBuildThreadId, Environment.CurrentManagedThreadId);
        CancellationToken token = _indexCancel!.Token;
        VorbisPageIndex? index = null;
        string? failure;
        try
        {
            using Stream stream = _openIndexStream!();
            index = VorbisPageIndex.TryBuild(stream, token, out failure);
            if (index is not null && (index.StartDrop != StartDrop || index.FinalGranule - index.StartDrop != TotalFrames))
            {
                failure = $"the index ends at frame {index.FinalGranule - index.StartDrop} after a start drop of {index.StartDrop}, but the decoder reports {TotalFrames} frames after a start drop of {StartDrop}.";
                index = null;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex.Message;
        }

        _builtIndex = index;
        _buildFailure = failure;
        Volatile.Write(ref _buildDone, 1);
    }

    // Takes the result of the index build on the thread that uses the decoder. It does not wait and does not allocate.
    private void AdoptIndexBuild()
    {
        if (SeekIndexState != EVorbisSeekIndexState.Building || Volatile.Read(ref _buildDone) == 0)
            return;

        if (_builtIndex is { } index)
        {
            _index = index;
            SeekIndexState = EVorbisSeekIndexState.Ready;
        }
        else
            FailIndex($"The Ogg page index was not built: {_buildFailure}");
        _builtIndex = null;
    }

    private static Func<Stream> OpenFileForIndex(string filePath)
        => () => new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);

    private const string NoSecondStreamReason = "The stream cannot be opened a second time, so the decoder has no Ogg page index.";

    private static Func<Stream>? GetIndexStreamSource(Stream stream)
    {
        if (stream is FileStream file)
            return OpenFileForIndex(file.Name);
        if (stream is MemoryStream memory && memory.TryGetBuffer(out ArraySegment<byte> buffer))
            return () => new MemoryStream(buffer.Array!, buffer.Offset, buffer.Count, writable: false);
        return null;
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
