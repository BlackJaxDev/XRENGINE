using System;
using System.Threading;

namespace XREngine.Timers;

/// <summary>
/// Coordinates the single collect-visible producer with the single render consumer.
/// Generation zero is the empty bootstrap buffer consumed by the first render.
/// </summary>
internal sealed class CollectVisibleGenerationGate
{
    private readonly ManualResetEventSlim _publicationChanged = new(true);

    private long _requestedGeneration;
    private long _completedGeneration;
    private long _publishedGeneration;
    private long _consumedGeneration = -1;
    private long _lastStaleReuseRequiredGeneration = -1;
    private int _terminated;

    public long RequestedGeneration => Volatile.Read(ref _requestedGeneration);
    public long CompletedGeneration => Volatile.Read(ref _completedGeneration);
    public long PublishedGeneration => Volatile.Read(ref _publishedGeneration);
    public long ConsumedGeneration => Volatile.Read(ref _consumedGeneration);
    public long RequiredGeneration => ConsumedGeneration + 1L;
    public bool IsTerminated => Volatile.Read(ref _terminated) != 0;
    public bool IsFreshGenerationAvailable => PublishedGeneration >= RequiredGeneration;

    public void Reset()
    {
        Volatile.Write(ref _requestedGeneration, 0L);
        Volatile.Write(ref _completedGeneration, 0L);
        Volatile.Write(ref _publishedGeneration, 0L);
        Volatile.Write(ref _consumedGeneration, -1L);
        Volatile.Write(ref _lastStaleReuseRequiredGeneration, -1L);
        Volatile.Write(ref _terminated, 0);
        _publicationChanged.Set();
    }

    public long RequestNextCollect()
    {
        if (IsTerminated)
            throw new InvalidOperationException("Cannot request visibility collection after the generation gate terminated.");

        return Interlocked.Increment(ref _requestedGeneration);
    }

    public void MarkCollectCompleted(long generation)
    {
        if (generation <= 0L || generation > RequestedGeneration)
            throw new InvalidOperationException($"Visibility collection completed invalid generation {generation}.");

        long previous = Interlocked.CompareExchange(ref _completedGeneration, generation, generation - 1L);
        if (previous != generation - 1L)
        {
            throw new InvalidOperationException(
                $"Visibility collection completed out of order. Expected {previous + 1L}, received {generation}.");
        }
    }

    public void Publish(long generation)
    {
        if (generation <= 0L || CompletedGeneration != generation)
        {
            throw new InvalidOperationException(
                $"Cannot publish visibility generation {generation}; completed generation is {CompletedGeneration}.");
        }

        long previous = Interlocked.CompareExchange(ref _publishedGeneration, generation, generation - 1L);
        if (previous != generation - 1L)
        {
            throw new InvalidOperationException(
                $"Visibility publication was out of order. Expected {previous + 1L}, received {generation}.");
        }

        _publicationChanged.Set();
    }

    public bool TryConsumeFresh(out long generation)
    {
        generation = RequiredGeneration;
        long published = PublishedGeneration;
        if (published < generation)
            return false;

        if (published != generation)
        {
            throw new InvalidOperationException(
                $"Visibility publication skipped a render generation. Required {generation}, published {published}.");
        }

        long previous = Interlocked.CompareExchange(ref _consumedGeneration, generation, generation - 1L);
        if (previous != generation - 1L)
        {
            generation = previous;
            return false;
        }

        _publicationChanged.Reset();
        if (IsTerminated || PublishedGeneration > generation)
            _publicationChanged.Set();

        return true;
    }

    public bool CanReusePreviousForRequiredGeneration(ECollectVisibleLatePolicy policy)
    {
        if (policy != ECollectVisibleLatePolicy.ReusePreviousVisibility || ConsumedGeneration < 0L)
            return false;

        long required = RequiredGeneration;
        return PublishedGeneration < required &&
               Volatile.Read(ref _lastStaleReuseRequiredGeneration) != required;
    }

    public bool TryRecordStaleReuse(long requiredGeneration)
    {
        if (requiredGeneration != RequiredGeneration)
            return false;

        long previous = Interlocked.Exchange(ref _lastStaleReuseRequiredGeneration, requiredGeneration);
        return previous != requiredGeneration;
    }

    public bool WaitForPublication()
    {
        _publicationChanged.Wait();
        return !IsTerminated;
    }

    public void Terminate()
    {
        Volatile.Write(ref _terminated, 1);
        _publicationChanged.Set();
    }
}
