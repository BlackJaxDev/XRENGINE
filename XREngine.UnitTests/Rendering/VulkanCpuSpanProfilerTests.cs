using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCpuSpanProfilerTests
{
    [Test]
    public void TargetedCapture_RetainsOnlyWarmedSelectedStageWithParentage()
    {
        VulkanCpuSpanProfiler.Configure([EVulkanCpuStage.PrimaryRecording, EVulkanCpuStage.SecondaryRecording], 8);
        VulkanCpuSpanProfiler.WarmCurrentThread();
        VulkanCpuSpanProfiler.Arm();
        try
        {
            using (VulkanRenderer.VulkanCpuStageScope primary = new(EVulkanCpuStage.PrimaryRecording))
            {
                using VulkanRenderer.VulkanCpuStageScope secondary = new(EVulkanCpuStage.SecondaryRecording);
            }
        }
        finally
        {
            VulkanCpuSpanProfiler.Disarm();
        }

        VulkanCpuSpanProfiler.VulkanCpuSpanRecord[] spans = VulkanCpuSpanProfiler.GetSnapshot();
        VulkanCpuSpanProfiler.VulkanCpuSpanRecord primarySpan = spans.Last(static span => span.Stage == EVulkanCpuStage.PrimaryRecording);
        VulkanCpuSpanProfiler.VulkanCpuSpanRecord secondarySpan = spans.Last(static span => span.Stage == EVulkanCpuStage.SecondaryRecording);
        primarySpan.ParentSpanId.ShouldBe(0);
        secondarySpan.ParentSpanId.ShouldBe(primarySpan.SpanId);
        primarySpan.EndTimestamp.ShouldBeGreaterThanOrEqualTo(primarySpan.StartTimestamp);
    }
}
