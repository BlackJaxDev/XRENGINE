using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

public sealed class RenderStatsVrPassTests
{
    [Test]
    public void VrRenderPassCounters_ArePublishedSeparatelyFromTotalFrameCounters()
    {
        bool previousTracking = Engine.Rendering.Stats.EnableTracking;
        try
        {
            Engine.Rendering.Stats.EnableTracking = true;
            Engine.Rendering.Stats.BeginFrame();
            Engine.Rendering.Stats.BeginFrame();

            Engine.Rendering.Stats.RenderPassCounters beforeVr = Engine.Rendering.Stats.Frame.CurrentCounters;
            Engine.Rendering.Stats.Frame.IncrementDrawCalls(4);
            Engine.Rendering.Stats.Frame.IncrementMultiDrawCalls(1);
            Engine.Rendering.Stats.Frame.AddTrianglesRendered(120);
            Engine.Rendering.Stats.Vr.RecordVrRenderPass(
                beforeVr,
                Engine.Rendering.Stats.Frame.CurrentCounters,
                TimeSpan.FromMilliseconds(2.5));

            Engine.Rendering.Stats.Frame.IncrementDrawCalls(3);
            Engine.Rendering.Stats.Frame.AddTrianglesRendered(45);

            Engine.Rendering.Stats.BeginFrame();

            Engine.Rendering.Stats.RenderPassCounters total = Engine.Rendering.Stats.Frame.LastCounters;
            Engine.Rendering.Stats.RenderPassCounters vr = Engine.Rendering.Stats.Vr.VrRenderPassCounters;
            Engine.Rendering.Stats.RenderPassCounters desktop =
                Engine.Rendering.Stats.RenderPassCounters.SubtractClamped(total, vr);

            total.DrawCalls.ShouldBe(7);
            total.MultiDrawCalls.ShouldBe(1);
            total.TrianglesRendered.ShouldBe(165);
            vr.DrawCalls.ShouldBe(4);
            vr.MultiDrawCalls.ShouldBe(1);
            vr.TrianglesRendered.ShouldBe(120);
            desktop.DrawCalls.ShouldBe(3);
            desktop.MultiDrawCalls.ShouldBe(0);
            desktop.TrianglesRendered.ShouldBe(45);
            Engine.Rendering.Stats.Vr.VrRenderPassTimeMs.ShouldBe(2.5, 0.01);
        }
        finally
        {
            Engine.Rendering.Stats.BeginFrame();
            Engine.Rendering.Stats.BeginFrame();
            Engine.Rendering.Stats.EnableTracking = previousTracking;
        }
    }
}
