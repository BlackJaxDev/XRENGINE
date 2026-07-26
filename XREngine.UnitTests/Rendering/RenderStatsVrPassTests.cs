using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

public sealed class RenderStatsVrPassTests
{
    [Test]
    public void VrRenderPassCounters_ArePublishedSeparatelyFromTotalFrameCounters()
    {
        bool previousTracking = RuntimeEngine.Rendering.Stats.EnableTracking;
        try
        {
            RuntimeEngine.Rendering.Stats.EnableTracking = true;
            RuntimeEngine.Rendering.Stats.BeginFrame();
            RuntimeEngine.Rendering.Stats.BeginFrame();

            RuntimeEngine.Rendering.Stats.RenderPassCounters beforeVr = RuntimeEngine.Rendering.Stats.Frame.CurrentCounters;
            RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(4);
            RuntimeEngine.Rendering.Stats.Frame.IncrementMultiDrawCalls(1);
            RuntimeEngine.Rendering.Stats.Frame.AddTrianglesRendered(120);
            RuntimeEngine.Rendering.Stats.Vr.RecordVrRenderPass(
                beforeVr,
                RuntimeEngine.Rendering.Stats.Frame.CurrentCounters,
                TimeSpan.FromMilliseconds(2.5));

            RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(3);
            RuntimeEngine.Rendering.Stats.Frame.AddTrianglesRendered(45);

            RuntimeEngine.Rendering.Stats.BeginFrame();

            RuntimeEngine.Rendering.Stats.RenderPassCounters total = RuntimeEngine.Rendering.Stats.Frame.LastCounters;
            RuntimeEngine.Rendering.Stats.RenderPassCounters vr = RuntimeEngine.Rendering.Stats.Vr.VrRenderPassCounters;
            RuntimeEngine.Rendering.Stats.RenderPassCounters desktop =
                RuntimeEngine.Rendering.Stats.RenderPassCounters.SubtractClamped(total, vr);

            total.DrawCalls.ShouldBe(7);
            total.MultiDrawCalls.ShouldBe(1);
            total.TrianglesRendered.ShouldBe(165);
            vr.DrawCalls.ShouldBe(4);
            vr.MultiDrawCalls.ShouldBe(1);
            vr.TrianglesRendered.ShouldBe(120);
            desktop.DrawCalls.ShouldBe(3);
            desktop.MultiDrawCalls.ShouldBe(0);
            desktop.TrianglesRendered.ShouldBe(45);
            RuntimeEngine.Rendering.Stats.Vr.VrRenderPassTimeMs.ShouldBe(2.5, 0.01);
        }
        finally
        {
            RuntimeEngine.Rendering.Stats.BeginFrame();
            RuntimeEngine.Rendering.Stats.BeginFrame();
            RuntimeEngine.Rendering.Stats.EnableTracking = previousTracking;
        }
    }
}
