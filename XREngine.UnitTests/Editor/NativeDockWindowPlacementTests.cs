using System.Text.Json;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeDockWindowPlacementTests
{
    private static readonly NativeDockWindowPlacement Fallback = new(160, 120, 960, 680);
    private static readonly RuntimeDesktopMonitor[] Monitors =
    [
        new(0, 0, 0, 1920, 1080, 0, 0, 1920, 1040, 1),
        new(0, 1920, 0, 1920, 1080, 1920, 0, 1920, 1040, 1),
    ];

    [Test]
    public void OldSavedWindowJsonKeepsPanelAndOptionalPlacement()
    {
        const string legacy = "[{\"Key\":\"detached:old\",\"Panel\":2}]";
        NativeDockSavedWindow[]? windows =
            JsonSerializer.Deserialize<NativeDockSavedWindow[]>(legacy);

        Assert.That(windows, Has.Length.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(windows![0].Panel, Is.EqualTo(2));
            Assert.That(windows[0].X, Is.Null);
            Assert.That(windows[0].Width, Is.Null);
        });
    }

    [Test]
    public void SavedWindowJsonRoundTripsClientPlacement()
    {
        NativeDockSavedWindow original = new("detached:new", PanelId: "assets",
            X: 2140, Y: 150, Width: 1100, Height: 760);
        NativeDockSavedWindow? restored = JsonSerializer.Deserialize<NativeDockSavedWindow>(
            JsonSerializer.Serialize(original));

        Assert.That(restored, Is.EqualTo(original));
    }

    [Test]
    public void OldRecordWithoutGeometryKeepsDefaultPlacement()
        => Assert.That(NativeDockWindowPlacement.Resolve(null, null, null, null,
            Monitors, Fallback), Is.EqualTo(Fallback));

    [Test]
    public void SavedClientBoundsRemainOnCurrentMonitor()
        => Assert.That(NativeDockWindowPlacement.Resolve(2140, 150, 1100, 760,
            Monitors, Fallback), Is.EqualTo(new NativeDockWindowPlacement(2140, 150, 1100, 760)));

    [Test]
    public void PartiallyOffscreenWindowKeepsVisibleTitleStrip()
        => Assert.That(NativeDockWindowPlacement.Resolve(1880, 1030, 960, 680,
            Monitors.AsSpan(0, 1), Fallback),
            Is.EqualTo(new NativeDockWindowPlacement(1856, 1008, 960, 680)));

    [Test]
    public void RemovedMonitorUsesDefaultPlacement()
        => Assert.That(NativeDockWindowPlacement.Resolve(2300, 120, 960, 680,
            Monitors.AsSpan(0, 1), Fallback), Is.EqualTo(Fallback));

    [Test]
    public void InvalidOrOversizedGeometryUsesSafeBounds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NativeDockWindowPlacement.Resolve(100, 100, 0, 680,
                Monitors, Fallback), Is.EqualTo(Fallback));
            Assert.That(NativeDockWindowPlacement.Resolve(100, 100, 9000, 680,
                Monitors, Fallback), Is.EqualTo(Fallback));
            Assert.That(NativeDockWindowPlacement.Resolve(100, 100, 3000, 2000,
                Monitors.AsSpan(0, 1), Fallback),
                Is.EqualTo(new NativeDockWindowPlacement(100, 100, 1920, 1040)));
        });
    }
}
