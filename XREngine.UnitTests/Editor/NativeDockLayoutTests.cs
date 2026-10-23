using NUnit.Framework;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public class NativeDockLayoutTests
{
    [Test]
    public void Sanitize_ClampsSizeAndSelectsAnOpenTab()
    {
        NativeDockLayout layout = new()
        {
            BottomSize = float.PositiveInfinity,
            OpenMask = 1 << 2,
            SelectedTab = 0,
            TabOrder = [3, 2, 1, 0],
        };

        layout.Sanitize();

        Assert.Multiple(() =>
        {
            Assert.That(layout.BottomSize, Is.EqualTo(240));
            Assert.That(layout.SelectedTab, Is.EqualTo(2));
            Assert.That(layout.TabOrder, Is.EqualTo(new[] { 3, 2, 1, 0 }));
        });
    }

    [Test]
    public void Sanitize_RejectsDuplicateTabOrder()
    {
        NativeDockLayout layout = new() { TabOrder = [0, 0, 1, 2] };

        layout.Sanitize();

        Assert.That(layout.TabOrder, Is.EqualTo(new[] { 0, 1, 2, 3 }));
    }

    [Test]
    public void Sanitize_RepairsInvalidPanelScrollOffsetsWithoutResettingValidState()
    {
        NativeDockLayout layout = new()
        {
            PanelScrollOffsets = [12, float.NaN, -4, 96],
            AssetFolder = "Game/Textures",
            AssetSearch = "stone",
            ConsoleFilter = "warning",
        };

        layout.Sanitize();

        Assert.Multiple(() =>
        {
            Assert.That(layout.PanelScrollOffsets, Is.EqualTo(new float[] { 12, 0, 0, 96 }));
            Assert.That(layout.AssetFolder, Is.EqualTo("Game/Textures"));
            Assert.That(layout.AssetSearch, Is.EqualTo("stone"));
            Assert.That(layout.ConsoleFilter, Is.EqualTo("warning"));
        });
    }
}
