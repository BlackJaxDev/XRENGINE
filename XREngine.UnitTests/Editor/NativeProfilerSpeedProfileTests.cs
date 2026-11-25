using NUnit.Framework;
using XREngine.Editor;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeProfilerSpeedProfileTests
{
    [Test]
    public void DurationAcceptsOnlyTheSharedCaptureRange()
    {
        SceneNode node = new("Profiler") { IsActiveSelf = false };
        try
        {
            node.NewChild<ProfilerPanel>(out ProfilerPanel panel);
            Assert.That(panel.SpeedProfileSeconds, Is.EqualTo(15));
            panel.SpeedProfileSeconds = NativeProfilerSpeedProfileSecondsBinding.MaximumSeconds;
            Assert.That(panel.SpeedProfileSeconds, Is.EqualTo(600));
            Assert.Throws<ArgumentOutOfRangeException>(() => panel.SpeedProfileSeconds = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => panel.SpeedProfileSeconds = 601);
            Assert.That(panel.SpeedProfileSeconds, Is.EqualTo(600));
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void InactivePanelRejectsADurationDraft()
    {
        SceneNode node = new("Profiler") { IsActiveSelf = false };
        try
        {
            node.NewChild<ProfilerPanel>(out ProfilerPanel panel);
            NativeProfilerSpeedProfileSecondsBinding binding = new(panel);
            Assert.That(binding.IsCurrent, Is.False);
            Assert.Throws<InvalidOperationException>(() => binding.SetValue("30"));
            Assert.That(panel.SpeedProfileSeconds, Is.EqualTo(15));
        }
        finally { node.Destroy(true); }
    }
}
