using NUnit.Framework;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks that a panel transfer cannot copy another panel's saved values.</summary>
[TestFixture]
public sealed class NativeDockPanelStateValuesTests
{
    [TestCase(NativeDockPanel.Assets)]
    [TestCase(NativeDockPanel.Console)]
    [TestCase(NativeDockPanel.Profiler)]
    [TestCase(NativeDockPanel.Animation)]
    public void TransferChangesOnlyTheNamedPanel(string panel)
    {
        NativeDockLayout source = new()
        {
            UseEngineAssets = true,
            AssetFolder = "Textures",
            AssetSearch = "stone",
            ConsoleFilter = "frame",
            ConsoleCategory = ELogCategory.Rendering,
            ConsoleAutoScroll = false,
            ProfilerView = (int)ENativeProfilerView.FrameOutputs,
            PanelScrollOffsets = [12, 24, 36, 48],
        };
        NativeDockLayout destination = new();
        NativeDockPanelStateValues[] before = NativeDockPanel.LegacyTools
            .Select(name => NativeDockPanelStateValues.Capture(destination, name)).ToArray();
        NativeDockPanelStateValues values = NativeDockPanelStateValues.Capture(source, panel);

        values.WriteTo(destination);

        Assert.That(values.Matches(destination), Is.True);
        foreach (NativeDockPanelStateValues other in before)
            if (other.Panel != panel)
                Assert.That(other.Matches(destination), Is.True, other.Panel);
    }

    [Test]
    public void CapturedValuesSurviveLaterSourceLayoutChanges()
    {
        NativeDockLayout source = new() { AssetFolder = "Meshes", AssetSearch = "crate" };
        source.PanelScrollOffsets[0] = 72;
        NativeDockPanelStateValues captured = NativeDockPanelStateValues.Capture(source,
            NativeDockPanel.Assets);
        source.AssetFolder = "Textures";
        source.AssetSearch = "wood";
        source.PanelScrollOffsets[0] = 144;
        NativeDockLayout destination = new();

        captured.WriteTo(destination);

        Assert.Multiple(() =>
        {
            Assert.That(destination.AssetFolder, Is.EqualTo("Meshes"));
            Assert.That(destination.AssetSearch, Is.EqualTo("crate"));
            Assert.That(destination.PanelScrollOffsets[0], Is.EqualTo(72));
            Assert.That(captured.Matches(source), Is.False);
        });
    }

    [Test]
    public void MatchingIgnoresOtherPanelsButDetectsItsOwnScrollAndFields()
    {
        NativeDockLayout layout = new();
        NativeDockPanelStateValues captured = NativeDockPanelStateValues.Capture(layout,
            NativeDockPanel.Console);
        layout.AssetFolder = "Unrelated";
        layout.PanelScrollOffsets[0] = 100;
        Assert.That(captured.Matches(layout), Is.True);
        Assert.That(captured.Matches(NativeDockPanelStateValues.Capture(layout,
            NativeDockPanel.Console)), Is.True);
        layout.ConsoleFilter = "new";
        Assert.That(captured.Matches(layout), Is.False);
        layout.ConsoleFilter = string.Empty;
        layout.PanelScrollOffsets[1] = 1;
        Assert.That(captured.Matches(layout), Is.False);
        Assert.That(captured.Matches(captured with { ScrollOffset = 1 }), Is.False);
        Assert.That(captured.Matches(captured with { Panel = NativeDockPanel.Assets }), Is.False);
    }

    [Test]
    public void PanelWithoutSavedFieldsCannotChangeToolStateOrDockStructure()
    {
        NativeDockLayout source = new() { AssetFolder = "Other", BottomSize = 600 };
        source.PanelScrollOffsets[0] = 100;
        NativeDockLayout destination = new NativeDockLayout().Sanitize();
        NativeDockTree tree = destination.DockTree!;
        float[] offsets = destination.PanelScrollOffsets;
        NativeDockPanelStateValues.Capture(source, NativeDockPanel.Scene).WriteTo(destination);
        Assert.Multiple(() =>
        {
            Assert.That(destination.AssetFolder, Is.Empty);
            Assert.That(destination.BottomSize, Is.EqualTo(240));
            Assert.That(destination.DockTree, Is.SameAs(tree));
            Assert.That(destination.PanelScrollOffsets, Is.SameAs(offsets));
            Assert.That(offsets, Is.EqualTo(new float[4]));
        });
    }

    [Test]
    public void TransferValidityUsesTheLiveSourceProof()
    {
        bool current = true;
        NativeDockPanelTransferState state = new(
            NativeDockPanelStateValues.Capture(new(), NativeDockPanel.Console), () => current);
        Assert.That(state.IsCurrent, Is.True);
        current = false;
        Assert.That(state.IsCurrent, Is.False);
    }
}
