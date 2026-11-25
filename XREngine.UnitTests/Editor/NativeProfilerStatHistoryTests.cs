using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeProfilerStatHistoryTests
{
    [Test]
    public void OneRenderFrameAddsOneSampleForEverySeries()
    {
        NativeProfilerStatHistory history = new();
        history.Set(ENativeProfilerStatSeries.VulkanPipelineBinds, 10);
        history.Set(ENativeProfilerStatSeries.XrWaitFrameBlockMs, 2.5);
        history.TryCommit(7).ShouldBeTrue();
        int revision = history.Revision;
        history.Set(ENativeProfilerStatSeries.VulkanPipelineBinds, 99);
        history.TryCommit(7).ShouldBeFalse();
        history.Revision.ShouldBe(revision);
        history.Count.ShouldBe(1);

        Span<float> samples = stackalloc float[4];
        history.CopyLatest(ENativeProfilerStatSeries.VulkanPipelineBinds, samples, 4).ShouldBe(1);
        samples[0].ShouldBe(10f);
        history.CopyLatest(ENativeProfilerStatSeries.XrWaitFrameBlockMs, samples, 4).ShouldBe(1);
        samples[0].ShouldBe(2.5f);
    }

    [Test]
    public void FullHistoryKeepsTheNewestSamplesOldestFirst()
    {
        NativeProfilerStatHistory history = new();
        int total = NativeProfilerStatHistory.Capacity + 3;
        for (int frame = 1; frame <= total; frame++)
        {
            history.Set(ENativeProfilerStatSeries.VulkanFrameTotalMs, frame);
            history.TryCommit((ulong)frame).ShouldBeTrue();
        }
        history.Count.ShouldBe(NativeProfilerStatHistory.Capacity);

        Span<float> samples = stackalloc float[3];
        history.CopyLatest(ENativeProfilerStatSeries.VulkanFrameTotalMs, samples, 3).ShouldBe(3);
        samples[0].ShouldBe(total - 2f);
        samples[2].ShouldBe(total);

        history.TryGetSummary(ENativeProfilerStatSeries.VulkanFrameTotalMs, 3,
            out float latest, out float average, out float maximum).ShouldBeTrue();
        latest.ShouldBe(total);
        average.ShouldBe(total - 1f);
        maximum.ShouldBe(total);
    }

    [Test]
    public void ClearAndInvalidInputLeaveNoSamples()
    {
        NativeProfilerStatHistory history = new();
        history.Set(ENativeProfilerStatSeries.VulkanWaitFenceMs, double.NaN);
        history.TryCommit(1).ShouldBeTrue();
        Span<float> samples = stackalloc float[1];
        history.CopyLatest(ENativeProfilerStatSeries.VulkanWaitFenceMs, samples, 1).ShouldBe(1);
        samples[0].ShouldBe(0f);
        history.CopyLatest(ENativeProfilerStatSeries.None, samples, 1).ShouldBe(0);

        int revision = history.Revision;
        history.Clear();
        history.Revision.ShouldNotBe(revision);
        history.Count.ShouldBe(0);
        history.TryGetSummary(ENativeProfilerStatSeries.VulkanWaitFenceMs, 10, out _, out _, out _).ShouldBeFalse();
        history.TryCommit(1).ShouldBeTrue();
    }
}
