using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Drives <see cref="TextureStreamingDecodeWidthController"/> with fake frames. Decode contention is modeled as extra
/// frame time for each active decode above the processors that the frame leaves free.
/// </summary>
[TestFixture]
public sealed class TextureStreamingDecodeWidthControllerTests
{
    private const double Period60Hz = 1000.0 / 60.0;

    private static TextureStreamingDecodeWidthController Create(int baseWidth, int? pinned = null)
        => new(baseWidth, TextureStreamingCapacityModel.MaxAutomaticDecodeConcurrency, pinned, 2.0);

    /// <summary>Runs frames with a full decode queue. Each active decode above <paramref name="freeCores"/> adds contention.</summary>
    private static int Run(TextureStreamingDecodeWidthController controller, int frames, int freeCores, double contentionPerDecode, int idleEvery = 4)
    {
        int width = controller.Width;
        for (int i = 0; i < frames; i++)
        {
            bool idle = idleEvery > 0 && i % idleEvery == 0;
            int active = idle ? 0 : width;
            double frame = 14.0 + Math.Max(0, active - freeCores) * contentionPerDecode;
            frame = Math.Max(Period60Hz, frame);
            width = controller.Update(new TextureStreamingDecodeSample(frame, Period60Hz, active, idle ? 0 : 20));
        }

        return width;
    }

    [TestCase(4, 1)]
    [TestCase(6, 2)]
    [TestCase(8, 4)]
    [TestCase(16, 12)]
    public void FullQueue_RaisesTheWidthOnlyAsFarAsTheFrameAllows(int processors, int freeCores)
    {
        // The topology base is 2 here; each decode above the free processors adds 3 ms, so the frame allows freeCores + 1
        // decodes inside the 2 ms rule. The feedback must find that width, not a fixed one.
        int baseWidth = TextureStreamingCapacityModel.ResolveBaseDecodeWidth(processors, Math.Min(4, processors - 1), 2, null);
        TextureStreamingDecodeWidthController controller = Create(baseWidth);

        int width = Run(controller, 900, freeCores, contentionPerDecode: 3.0);

        int allowed = Math.Min(TextureStreamingCapacityModel.MaxAutomaticDecodeConcurrency, freeCores + 1);
        width.ShouldBeGreaterThanOrEqualTo(Math.Max(1, allowed - 1));
        width.ShouldBeLessThanOrEqualTo(Math.Min(TextureStreamingCapacityModel.MaxAutomaticDecodeConcurrency, freeCores + 2));
    }

    [Test]
    public void FastMachine_ReachesTheMemoryBoundQuickly()
    {
        TextureStreamingDecodeWidthController controller = Create(2);

        int width = Run(controller, 30, freeCores: 16, contentionPerDecode: 3.0);

        width.ShouldBe(TextureStreamingCapacityModel.MaxAutomaticDecodeConcurrency);
        controller.BackoffCount.ShouldBe(0);
    }

    [Test]
    public void WeakMachine_GoesBelowTheOldWidth_OnlyOnFrameEvidence()
    {
        // One free processor: every second decode costs 6 ms of frame time.
        TextureStreamingDecodeWidthController controller = Create(2);

        int width = Run(controller, 600, freeCores: 1, contentionPerDecode: 6.0);

        width.ShouldBe(1);
        controller.BackoffCount.ShouldBeGreaterThan(0);
        controller.HasRecentEvidence.ShouldBeTrue();
    }

    [Test]
    public void WeakMachine_DoesNotClimbBackToTheBound_AfterEvidence()
    {
        // One free processor, and decodes start from the memory bound (a fast base). The evidence must keep the width low
        // for the evidence memory, not let the rise bring it back.
        TextureStreamingDecodeWidthController controller = new(8, 8, null, 2.0);

        Run(controller, 600, freeCores: 1, contentionPerDecode: 4.0);

        controller.Width.ShouldBeLessThanOrEqualTo(2);
    }

    [Test]
    public void NoEvidence_KeepsTheOldWidthOfTwo()
    {
        // The frame does not get slower with decodes, but the queue is never saturated above the old width.
        TextureStreamingDecodeWidthController controller = Create(2);
        for (int i = 0; i < 600; i++)
            controller.Update(new TextureStreamingDecodeSample(Period60Hz, Period60Hz, i % 3 == 0 ? 0 : 1, 0));

        controller.Width.ShouldBe(2);
        controller.BackoffCount.ShouldBe(0);
    }

    [Test]
    public void SceneCut_ToAHeavierShot_IsNotEvidence()
    {
        TextureStreamingDecodeWidthController controller = Create(2);
        Run(controller, 120, freeCores: 16, contentionPerDecode: 0.0);
        int before = controller.Width;

        // The new shot costs 24 ms with or without decodes, and decodes run in every frame.
        controller.MarkDiscontinuity();
        for (int i = 0; i < 120; i++)
            controller.Update(new TextureStreamingDecodeSample(24.0, Period60Hz, controller.Width, 20));

        controller.BackoffCount.ShouldBe(0);
        controller.Width.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Test]
    public void ExplicitOverride_PinsTheWidth()
    {
        TextureStreamingDecodeWidthController controller = Create(2, pinned: 3);

        Run(controller, 300, freeCores: 1, contentionPerDecode: 6.0).ShouldBe(3);
        Run(controller, 300, freeCores: 16, contentionPerDecode: 0.0).ShouldBe(3);
    }
}
