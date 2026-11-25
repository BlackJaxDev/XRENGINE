using NUnit.Framework;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public class NativeDockLayoutTests
{
    [Test]
    public void FreshLayout_SanitizeCreatesUsableDockTree()
    {
        NativeDockTree? tree = new NativeDockLayout().Sanitize().DockTree;

        Assert.That(tree, Is.Not.Null);
        Assert.That(tree!.Stacks().SelectMany(stack => stack.Tabs),
            Does.Contain(NativeDockPanel.Scene));
    }

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

    [TestCase(-1)]
    [TestCase(10_000)]
    public void Sanitize_RepairsAnUnknownProfilerView(int view)
    {
        NativeDockLayout layout = new() { ProfilerView = view };

        layout.Sanitize();

        Assert.That(layout.ProfilerView, Is.EqualTo((int)ENativeProfilerView.CpuTimings));
    }

    [Test]
    public void PanelViewState_SurvivesSerializationAndSanitize()
    {
        NativeDockLayout layout = new()
        {
            ProfilerView = (int)ENativeProfilerView.FpsSpikes,
            ConsoleCategory = ELogCategory.Rendering,
            ConsoleAutoScroll = false,
        };

        NativeDockLayout? restored = System.Text.Json.JsonSerializer.Deserialize<NativeDockLayout>(
            System.Text.Json.JsonSerializer.Serialize(layout))?.Sanitize();

        Assert.That(restored, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(restored!.ProfilerView, Is.EqualTo((int)ENativeProfilerView.FpsSpikes));
            Assert.That(restored.ConsoleCategory, Is.EqualTo(ELogCategory.Rendering));
            Assert.That(restored.ConsoleAutoScroll, Is.False);
        });
    }

    [Test]
    public void Sanitize_ShowsAllCategoriesForAnUnknownConsoleCategory()
    {
        NativeDockLayout layout = new() { ConsoleCategory = (ELogCategory)10_000 };

        layout.Sanitize();

        Assert.That(layout.ConsoleCategory, Is.Null);
    }

    [Test]
    public void FreshLayout_FollowsNewConsoleEntriesAndShowsAllCategories()
    {
        NativeDockLayout layout = new NativeDockLayout().Sanitize();

        Assert.That(layout.ConsoleAutoScroll, Is.True);
        Assert.That(layout.ConsoleCategory, Is.Null);
    }

    [Test]
    public void LegacyLayout_MigratesToVersionedTreeWithoutLosingPanelState()
    {
        NativeDockLayout legacy = new()
        {
            OpenMask = (1 << 0) | (1 << 2),
            SelectedTab = 2,
            TabOrder = [2, 0, 3, 1],
            BottomSize = 360,
        };

        NativeDockTree tree = legacy.Sanitize().DockTree!;
        string[] open = tree.Stacks().SelectMany(stack => stack.Tabs).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(tree.Version, Is.EqualTo(NativeDockTree.CurrentVersion));
            Assert.That(open, Does.Contain(NativeDockPanel.Hierarchy));
            Assert.That(open, Does.Contain(NativeDockPanel.Scene));
            Assert.That(open, Does.Contain(NativeDockPanel.Inspector));
            Assert.That(open, Does.Contain(NativeDockPanel.Profiler));
            Assert.That(tree.ClosedPanels, Does.Contain(NativeDockPanel.Console));
            Assert.That(tree.Root.Second!.First!.FixedSize, Is.EqualTo(360));
        });
    }

    [Test]
    public void LegacyDetached_MigratesOneToolStackDespiteStalePrimaryTree()
    {
        NativeDockLayout layout = new()
        {
            OpenMask = (1 << 0) | (1 << 2),
            SelectedTab = 2,
            TabOrder = [2, 0, 3, 1],
            PanelScrollOffsets = [17, 23, 31, 47],
            AssetSearch = "stone",
            ConsoleFilter = "warning",
            DockTree = new NativeDockLayout().Sanitize().DockTree,
        };

        NativeDockTree tree = NativeDockTree.FromLegacyDetached(layout.Sanitize(), 0);
        Assert.Multiple(() =>
        {
            Assert.That(tree.Stacks().Count(), Is.EqualTo(1));
            Assert.That(tree.Root.Tabs, Is.EqualTo(new[] { NativeDockPanel.Profiler, NativeDockPanel.Assets }));
            Assert.That(tree.Root.Selected, Is.EqualTo(NativeDockPanel.Profiler));
            Assert.That(tree.Root.Tabs, Does.Not.Contain(NativeDockPanel.Scene));
            Assert.That(layout.PanelScrollOffsets, Is.EqualTo(new float[] { 17, 23, 31, 47 }));
            Assert.That(layout.AssetSearch, Is.EqualTo("stone"));
            Assert.That(layout.ConsoleFilter, Is.EqualTo("warning"));
        });
    }

    [Test]
    public void DockTree_MoveSplitCloseReopenAndResizePreserveUniquePanelIdentity()
    {
        NativeDockTree tree = new NativeDockLayout().Sanitize().DockTree!;
        NativeDockNode scene = tree.Stacks().Single(stack => stack.Tabs.Contains(NativeDockPanel.Scene));
        NativeDockNode hierarchy = tree.Stacks().Single(stack => stack.Tabs.Contains(NativeDockPanel.Hierarchy));

        Assert.That(tree.Move(NativeDockPanel.Console, scene.Id, 0), Is.True);
        Assert.That(tree.SplitPanel(NativeDockPanel.Profiler, hierarchy.Id, vertical: true, before: true), Is.True);
        Assert.That(tree.Close(NativeDockPanel.Assets), Is.True);
        Assert.That(tree.Reopen(NativeDockPanel.Assets, hierarchy.Id), Is.True);
        Assert.That(tree.Resize(tree.Root.Id, 420), Is.True);
        tree.Sanitize();

        Assert.Multiple(() =>
        {
            Assert.That(tree.Stacks().SelectMany(stack => stack.Tabs).Count(), Is.EqualTo(NativeDockPanel.All.Length));
            Assert.That(tree.Stacks().SelectMany(stack => stack.Tabs).Distinct().Count(), Is.EqualTo(NativeDockPanel.All.Length));
            Assert.That(tree.Root.FixedSize, Is.EqualTo(420));
        });
    }

    [Test]
    public void DockTree_RepeatedSplitMovesCollapseEmptyBranches()
    {
        NativeDockTree tree = new NativeDockLayout().Sanitize().DockTree!;
        string sceneId = tree.Stacks().Single(stack => stack.Tabs.Contains(NativeDockPanel.Scene)).Id;

        for (int index = 0; index < 200; index++)
            Assert.That(tree.SplitPanel(NativeDockPanel.Assets, sceneId, vertical: index % 2 == 0,
                before: index % 2 == 0), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(tree.Stacks().Count(), Is.LessThanOrEqualTo(5));
            Assert.That(tree.Sanitize().Stacks().SelectMany(stack => stack.Tabs)
                .Count(panel => panel == NativeDockPanel.Assets), Is.EqualTo(1));
        });
    }

    [Test]
    public void DockTree_NullCollectionsRepairAndIncompleteSplitFallsBack()
    {
        NativeDockTree nullCollections = new()
        {
            ClosedPanels = null!,
            Root = new NativeDockNode { Id = null!, Tabs = null! },
        };
        Assert.DoesNotThrow(() => nullCollections.Sanitize());
        Assert.That(nullCollections.Root.Id, Is.Not.Empty);
        Assert.That(nullCollections.ClosedPanels.Count, Is.EqualTo(NativeDockPanel.All.Length));

        NativeDockLayout malformed = new()
        {
            DockTree = new NativeDockTree
            {
                Root = new NativeDockNode { First = new NativeDockNode(), Second = null },
            },
        };
        Assert.DoesNotThrow(() => malformed.Sanitize());
        Assert.That(malformed.DockTree!.Stacks().SelectMany(stack => stack.Tabs),
            Does.Contain(NativeDockPanel.Scene));
    }
}
