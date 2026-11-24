using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Checks the non-blocking GPU timestamp ring of the OpenGL apply lane with fake query results.
/// </summary>
[TestFixture]
public sealed class TextureStreamingGpuTimerTests
{
    private sealed class FakeTimestampSource(int queryCount) : ITextureStreamingTimestampSource
    {
        private readonly ulong[] _values = new ulong[queryCount];
        private readonly bool[] _ready = new bool[queryCount];
        private ulong _clock;

        public int QueryCount => queryCount;

        public int Writes { get; private set; }

        public bool FailWrites { get; set; }

        public bool TryWriteTimestamp(int query)
        {
            if (FailWrites)
                return false;

            Writes++;
            _values[query] = _clock;
            _ready[query] = false;
            return true;
        }

        public ERenderQueryReadStatus TryReadTimestamp(int query, out ulong nanoseconds)
        {
            nanoseconds = _values[query];
            return _ready[query] ? ERenderQueryReadStatus.Ready : ERenderQueryReadStatus.NotReady;
        }

        public void AdvanceGpu(double milliseconds)
            => _clock += (ulong)(milliseconds * 1_000_000.0);

        public void ResolveAll()
            => Array.Fill(_ready, true);
    }

    [Test]
    public void Pair_IsReadOnlyWhenItsResultsAreReady_AndGivesTheGpuTimePerItem()
    {
        FakeTimestampSource source = new(4);
        TextureStreamingGpuTimer timer = new(source);

        int pair = timer.Begin();
        source.AdvanceGpu(6.0);
        timer.End(pair, items: 2);

        timer.TryReadCompleted(out _).ShouldBeFalse();

        source.ResolveAll();
        timer.TryReadCompleted(out double millisecondsPerItem).ShouldBeTrue();
        millisecondsPerItem.ShouldBe(3.0, tolerance: 1e-9);
        timer.TryReadCompleted(out _).ShouldBeFalse();
        timer.ReadPairs.ShouldBe(1);
    }

    [Test]
    public void FullRing_SkipsTiming_InsteadOfWaiting()
    {
        FakeTimestampSource source = new(4);
        TextureStreamingGpuTimer timer = new(source);

        int first = timer.Begin();
        timer.End(first, 1);
        int second = timer.Begin();
        timer.End(second, 1);

        timer.Begin().ShouldBe(-1);
        source.Writes.ShouldBe(4);

        source.ResolveAll();
        while (timer.TryReadCompleted(out _))
        {
        }

        timer.Begin().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void ResultThatNeverComes_IsDropped()
    {
        FakeTimestampSource source = new(2);
        TextureStreamingGpuTimer timer = new(source);
        timer.End(timer.Begin(), 1);

        for (int i = 0; i <= TextureStreamingGpuTimer.MaxPendingPolls; i++)
            timer.TryReadCompleted(out _).ShouldBeFalse();

        timer.DroppedPairs.ShouldBe(1);
        timer.Begin().ShouldBe(0);
    }

    [Test]
    public void FailedWrite_TimesNothing()
    {
        FakeTimestampSource source = new(2) { FailWrites = true };
        TextureStreamingGpuTimer timer = new(source);

        int pair = timer.Begin();
        pair.ShouldBe(-1);
        timer.End(pair, 1);
        timer.TryReadCompleted(out _).ShouldBeFalse();
    }

    [Test]
    public void GpuCost_RaisesTheEffectiveWork_AndTheCostOfTheItems()
    {
        // The CPU timer sees 0.5 ms for each apply, but the GPU timestamps show 8 ms.
        TextureStreamingWorkController controller = new(new TextureStreamingWorkControllerOptions(1, 16, 2.0));
        for (int i = 0; i < 30; i++)
            controller.Update(new TextureStreamingWorkSample(1000.0 / 60.0, 6.0, 1000.0 / 60.0, 0.0, 0, false, 0.5));
        for (int i = 0; i < 4; i++)
            controller.ObserveGpuItemCost(8.0);

        controller.Update(new TextureStreamingWorkSample(28.0, 28.0, 1000.0 / 60.0, 1.0, 2, true, 0.5));

        controller.GpuCostPerItemMilliseconds.ShouldBe(8.0, tolerance: 1e-9);
        controller.BackoffCount.ShouldBe(1);
        controller.ItemsThisFrame.ShouldBe(1);
    }
}
