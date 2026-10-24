using NUnit.Framework;
using Shouldly;
using XREngine.Editor;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeProfilerVulkanTelemetryRowsTests
{
    [Test]
    public void OnePublicationIncludesEveryStageAndBoundedCausalWaits()
    {
        List<NativeProfilerDisplayRow> rows = [];
        NativeProfilerVulkanTelemetryRows.Append(rows, default(VulkanFrameTelemetryPublication));
        rows.ShouldBeEmpty();

        VulkanFrameCausalWaitSet waits = default;
        waits.Add(new VulkanFrameCausalWait(EVulkanFrameWaitReason.FrameSlot,
            TimeSpan.FromMilliseconds(2), 42, 1, 0, 9, 4, 2, 3, 1,
            EVulkanFrameStage.FramePacing));
        waits.AddDropped(5);
        VulkanFrameTelemetryPublication frame = default(VulkanFrameTelemetryPublication) with
        {
            AuthorityId = 7,
            PublicationSequence = 3,
            CausalWaits = waits,
            Tree = new VulkanFrameTreeTelemetry(
                TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(7),
                TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(4),
                TimeSpan.FromMilliseconds(2), TimeSpan.Zero, TimeSpan.Zero,
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(6)),
        };
        NativeProfilerVulkanTelemetryRows.Append(rows, frame);

        rows.Count.ShouldBe(11 + (int)EVulkanFrameStage.Count + 2);
        rows[0].Scope.ShouldBe("Vulkan frame publication");
        rows[1].Total.ShouldBe("6.000");
        rows.Count(row => row.Scope.StartsWith("Vulkan stage:", StringComparison.Ordinal))
            .ShouldBe((int)EVulkanFrameStage.Count);
        rows[^2].Self.ShouldContain("dropped 5");
        rows[^1].Scope.ShouldContain("FramePacing/FrameSlot");
        rows[^1].Total.ShouldBe("2.000");
    }
}
