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

    [Test]
    public void BvhGpuProfiler_BoundsPendingAndAbandonedTimestampScopes()
    {
        BvhGpuProfiler.HasPendingCapacity(pending: 1023, abandoned: 0, reserved: 0).ShouldBeTrue();
        BvhGpuProfiler.HasPendingCapacity(pending: 512, abandoned: 511, reserved: 1).ShouldBeFalse();
        BvhGpuProfiler.HasPendingCapacity(pending: 0, abandoned: 1024, reserved: 0).ShouldBeFalse();
        BvhGpuProfiler.HasPendingCapacity(pending: -1, abandoned: 0, reserved: 0).ShouldBeFalse();
    }
}
