using NUnit.Framework;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeHierarchySelectionTests
{
    [Test]
    public void SelectionUpdatesOffscreenAccessibilityWithoutCreatingRows()
    {
        UIVirtualTreeProvider tree = CreateTree();
        SceneNode first = new("First"), last = new("Last");
        Dictionary<Guid, ulong> ids = new() { [first.ID] = 1, [last.ID] = 1000 };
        Dictionary<ulong, SceneNode> nodes = new() { [1] = first, [1000] = last };
        NativeHierarchySelection selection = new();
        try
        {
            Assert.That(selection.Synchronize(tree, ids, nodes, [last]), Is.True);
            Assert.That(tree.TryGetAccessibilityItem(999, out UIVirtualAccessibilityItem item), Is.True);
            Assert.That(item.IsSelected, Is.True);
            Assert.That(tree.ActiveId, Is.EqualTo(1000UL));
            ulong revision = tree.Revision;
            Assert.That(selection.Synchronize(tree, ids, nodes, [last]), Is.False);
            Assert.That(tree.Revision, Is.EqualTo(revision));
            selection.Synchronize(tree, ids, nodes, [first]);
            Assert.That(tree.IsSelected(1000), Is.False);
            Assert.That(tree.IsSelected(1), Is.True);
            selection.Synchronize(tree, ids, nodes, []);
            Assert.That(tree.IsSelected(1), Is.False);
            Assert.That(tree.ActiveId, Is.Zero);
        }
        finally { first.Destroy(true); last.Destroy(true); }
    }

    [Test]
    public void BulkRebuildRestoresStableSelectionAndRejectsForeignNodeMapping()
    {
        UIVirtualTreeProvider tree = CreateTree();
        SceneNode selected = new("Selected"), foreign = new("Foreign");
        Dictionary<Guid, ulong> ids = new() { [selected.ID] = 1, [foreign.ID] = 1 };
        Dictionary<ulong, SceneNode> nodes = new() { [1] = selected };
        NativeHierarchySelection selection = new();
        try
        {
            selection.Synchronize(tree, ids, nodes, [selected]);
            tree.BeginBulkLoad();
            tree.Insert(1, 0, 0, "Renamed");
            tree.EndBulkLoad();
            selection.Synchronize(tree, ids, nodes, [selected], reset: true);
            Assert.That(tree.IsSelected(1), Is.True);
            selection.Synchronize(tree, ids, nodes, [foreign]);
            Assert.That(tree.IsSelected(1), Is.False);
            Assert.That(tree.ActiveId, Is.Zero);
            selected.Destroy(true);
            selection.Synchronize(tree, ids, nodes, [selected]);
            Assert.That(tree.IsSelected(1), Is.False);
        }
        finally { selected.Destroy(true); foreign.Destroy(true); }
    }

    private static UIVirtualTreeProvider CreateTree()
    {
        UIVirtualTreeProvider tree = new(
            () => throw new AssertionException("Selection must not realize rows."),
            (_, _, _, _) => throw new AssertionException("Selection must not bind rows."),
            (_, _, _) => throw new AssertionException("Selection must not recycle rows."));
        tree.BeginBulkLoad();
        for (ulong id = 1; id <= 1000; id++)
            tree.Insert(id, 0, int.MaxValue, "Node " + id);
        tree.EndBulkLoad();
        return tree;
    }
}
