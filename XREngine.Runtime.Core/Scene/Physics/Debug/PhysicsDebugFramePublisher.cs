using System.Diagnostics;
using System.Threading;

namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Lock-free single-producer frame publisher backed by three reusable slots.
/// Readers pin only the slot they are actively copying.
/// </summary>
public sealed class PhysicsDebugFramePublisher
{
    private const int SlotCount = 3;
    private readonly PhysicsDebugFrameStorage[] _slots =
    [
        new(),
        new(),
        new(),
    ];
    private readonly PhysicsDebugFrameWriter[] _writers =
    [
        new(),
        new(),
        new(),
    ];
    private int _publishedIndex = -1;
    private int _nextWriteIndex;
    private long _nextGeneration;
    private long _droppedPublications;
    private PhysicsDebugBudget _budget = PhysicsDebugBudget.Default;

    public PhysicsDebugBudget Budget
    {
        get => _budget;
        set => _budget = value.ClampNonNegative();
    }

    public long DroppedPublications => Volatile.Read(ref _droppedPublications);

    public PhysicsDebugFrameWriter? BeginWrite(
        PhysicsDebugSource source,
        PhysicsDebugDepthMode depthMode,
        int sourcePointCount = 0,
        int sourceLineCount = 0,
        int sourceTriangleCount = 0)
    {
        int publishedIndex = Volatile.Read(ref _publishedIndex);
        for (int offset = 0; offset < SlotCount; offset++)
        {
            int index = (_nextWriteIndex + offset) % SlotCount;
            PhysicsDebugFrameStorage slot = _slots[index];
            if (index == publishedIndex || Volatile.Read(ref slot.ReaderCount) != 0)
                continue;

            _nextWriteIndex = (index + 1) % SlotCount;
            slot.Begin(
                source,
                depthMode,
                sourcePointCount,
                sourceLineCount,
                sourceTriangleCount,
                Stopwatch.GetTimestamp());
            PhysicsDebugFrameWriter writer = _writers[index];
            writer.Activate(this, slot, _budget);
            return writer;
        }

        Interlocked.Increment(ref _droppedPublications);
        return null;
    }

    public bool TryAcquireLatest(out PhysicsDebugFrameLease lease)
    {
        for (int attempt = 0; attempt < SlotCount; attempt++)
        {
            int index = Volatile.Read(ref _publishedIndex);
            if (index < 0)
                break;

            PhysicsDebugFrameStorage slot = _slots[index];
            Interlocked.Increment(ref slot.ReaderCount);
            if (index == Volatile.Read(ref _publishedIndex))
            {
                lease = new PhysicsDebugFrameLease(this, slot);
                return true;
            }

            Interlocked.Decrement(ref slot.ReaderCount);
        }

        lease = default;
        return false;
    }

    internal void Publish(PhysicsDebugFrameStorage storage)
    {
        long publicationStart = Stopwatch.GetTimestamp();
        long generation = Interlocked.Increment(ref _nextGeneration);
        storage.Generation = generation;
        int storageIndex = Array.IndexOf(_slots, storage);
        storage.Telemetry = new PhysicsDebugFrameTelemetry(
            generation,
            storage.Source,
            storage.SourcePointCount,
            storage.SourceLineCount,
            storage.SourceTriangleCount,
            storage.PointCount,
            storage.LineCount,
            storage.TriangleCount,
            storage.DroppedPointCount + Math.Max(0, storage.SourcePointCount - storage.PointCount - storage.DroppedPointCount),
            storage.DroppedLineCount + Math.Max(0, storage.SourceLineCount - storage.LineCount - storage.DroppedLineCount),
            storage.DroppedTriangleCount + Math.Max(0, storage.SourceTriangleCount - storage.TriangleCount - storage.DroppedTriangleCount),
            storage.PublishedByteCount,
            storage.ExtractionTicks,
            Stopwatch.GetTimestamp() - publicationStart);
        Volatile.Write(ref _publishedIndex, storageIndex);
    }

    internal void Release(PhysicsDebugFrameStorage storage)
        => Interlocked.Decrement(ref storage.ReaderCount);
}
