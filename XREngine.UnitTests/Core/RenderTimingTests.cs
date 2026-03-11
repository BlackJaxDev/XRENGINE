using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Core;

public sealed class RenderTimingTests
{
    [Test]
    public void AbstractRenderer_ShouldSkipImGuiFrame_UsesExactTickIdentity()
    {
        AbstractRenderer.ShouldSkipImGuiFrame(allowMultipleInFrame: false, timestampTicks: 100L, lastTimestampTicks: 100L).ShouldBeTrue();
        AbstractRenderer.ShouldSkipImGuiFrame(allowMultipleInFrame: false, timestampTicks: 101L, lastTimestampTicks: 100L).ShouldBeFalse();
        AbstractRenderer.ShouldSkipImGuiFrame(allowMultipleInFrame: true, timestampTicks: 100L, lastTimestampTicks: 100L).ShouldBeFalse();
    }

    [Test]
    public void BvhGpuProfiler_ShouldResetFrameAccumulator_OnTickChangeOnly()
    {
        BvhGpuProfiler.ShouldResetFrameAccumulator(initializedFrameStamp: false, currentFrameTimestampTicks: 100L, nextFrameTimestampTicks: 101L).ShouldBeFalse();
        BvhGpuProfiler.ShouldResetFrameAccumulator(initializedFrameStamp: true, currentFrameTimestampTicks: 100L, nextFrameTimestampTicks: 100L).ShouldBeFalse();
        BvhGpuProfiler.ShouldResetFrameAccumulator(initializedFrameStamp: true, currentFrameTimestampTicks: 100L, nextFrameTimestampTicks: 101L).ShouldBeTrue();
    }
}