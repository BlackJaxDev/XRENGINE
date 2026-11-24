namespace XREngine.Rendering.Meshlets;

/// <summary>
/// Moves the geometry page feedback of completed GPU frames from the render thread to the
/// publisher thread. The backend reads the page requests and slot touch bits of a frame after the
/// GPU has finished it and calls <see cref="Enqueue"/>. The publisher calls <see cref="DrainTo"/>
/// before each streaming update, which gives every queued frame to
/// <see cref="MeshletPageStreamingManager.SubmitFeedback"/>.
/// </summary>
/// <remarks>
/// The queue holds a fixed number of frames. Each frame entry keeps its own request and touch-word
/// arrays, which grow only when a frame has more data than any frame before it, so a steady frame
/// stream does not allocate. When the publisher does not drain the queue in time, a new frame
/// replaces the oldest queued frame, because newer feedback describes the current view better.
/// <see cref="DroppedFrames"/> counts the replaced frames. All members are thread-safe.
/// </remarks>
public sealed class MeshletPageFeedbackQueue
{
    /// <summary>The default number of frames that the queue holds.</summary>
    public const int DefaultFrameCapacity = 4;

    private readonly Lock _sync = new();
    private readonly Frame[] _frames;
    private int _head;
    private int _count;
    private long _droppedFrames;
    private long _enqueuedFrames;

    private sealed class Frame
    {
        public MeshletPageRequest[] Requests = [];
        public int RequestCount;
        public uint[] TouchWords = [];
        public int TouchWordCount;
        public long OverflowCount;
        public long FeedbackFrame;
    }

    /// <summary>Creates a queue that holds <paramref name="frameCapacity"/> frames.</summary>
    public MeshletPageFeedbackQueue(int frameCapacity = DefaultFrameCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCapacity, 1);
        _frames = new Frame[frameCapacity];
        for (int index = 0; index < _frames.Length; ++index)
            _frames[index] = new Frame();
    }

    /// <summary>The number of frames that the queue holds.</summary>
    public int FrameCapacity => _frames.Length;

    /// <summary>The number of queued frames.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _count;
        }
    }

    /// <summary>The frames that a newer frame replaced before the publisher drained them.</summary>
    public long DroppedFrames
    {
        get
        {
            lock (_sync)
                return _droppedFrames;
        }
    }

    /// <summary>The frames that <see cref="Enqueue"/> accepted since the queue was created.</summary>
    public long EnqueuedFrames
    {
        get
        {
            lock (_sync)
                return _enqueuedFrames;
        }
    }

    /// <summary>
    /// Copies the feedback of one completed GPU frame into the queue. When the queue is full, the
    /// oldest queued frame is replaced. The spans can point into mapped GPU memory; the queue does
    /// not keep them after the call.
    /// </summary>
    /// <param name="requests">The page requests that the GPU wrote.</param>
    /// <param name="overflowCount">The requests that did not fit in the GPU request buffer.</param>
    /// <param name="touchWords">The slot touch bits: bit <c>s % 32</c> of word <c>s / 32</c> is slot <c>s</c>.</param>
    /// <param name="feedbackFrame">The render frame that wrote the feedback.</param>
    public void Enqueue(ReadOnlySpan<MeshletPageRequest> requests, long overflowCount, ReadOnlySpan<uint> touchWords, long feedbackFrame)
    {
        lock (_sync)
        {
            int index;
            if (_count == _frames.Length)
            {
                index = _head;
                _head = (_head + 1) % _frames.Length;
                ++_droppedFrames;
            }
            else
            {
                index = (_head + _count) % _frames.Length;
                ++_count;
            }

            Frame frame = _frames[index];
            if (frame.Requests.Length < requests.Length)
                frame.Requests = new MeshletPageRequest[requests.Length];
            requests.CopyTo(frame.Requests);
            frame.RequestCount = requests.Length;
            if (frame.TouchWords.Length < touchWords.Length)
                frame.TouchWords = new uint[touchWords.Length];
            touchWords.CopyTo(frame.TouchWords);
            frame.TouchWordCount = touchWords.Length;
            frame.OverflowCount = overflowCount;
            frame.FeedbackFrame = feedbackFrame;
            ++_enqueuedFrames;
        }
    }

    /// <summary>
    /// Gives every queued frame, oldest first, to <paramref name="manager"/> and empties the queue.
    /// Call it on the publisher thread before <see cref="MeshletPageStreamingManager.Update"/>.
    /// Returns the number of frames given.
    /// </summary>
    public int DrainTo(MeshletPageStreamingManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        lock (_sync)
        {
            int drained = _count;
            for (int offset = 0; offset < _count; ++offset)
            {
                Frame frame = _frames[(_head + offset) % _frames.Length];
                manager.SubmitFeedback(
                    frame.Requests.AsSpan(0, frame.RequestCount),
                    frame.OverflowCount,
                    frame.TouchWords.AsSpan(0, frame.TouchWordCount),
                    frame.FeedbackFrame);
            }
            _head = 0;
            _count = 0;
            return drained;
        }
    }

    /// <summary>Removes every queued frame without giving it to a manager.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _head = 0;
            _count = 0;
        }
    }
}
