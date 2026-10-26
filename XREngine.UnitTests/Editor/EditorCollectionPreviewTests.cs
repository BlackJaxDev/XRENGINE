using System.Collections;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public sealed class EditorCollectionPreviewTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void FormattingPreservesANewerDisplayWriteOrTransferredRow(bool transferRow)
    {
        using UITestShaderScope shaders = new();
        EditorCollectionToStringTarget target = new();
        SceneNode detail = Create(target);
        SceneNode other = new("Other preview owner");
        other.SetTransform<UIListTransform>();
        UITextComponent? captured = null;
        string? before = null;
        bool changed = false;
        UIListTransform list = Nodes(detail).Select(node => node.Transform).OfType<UIListTransform>()
            .Single(transform => transform.VirtualProvider is not null);
        target.OnToString = () =>
        {
            if (changed) return;
            changed = true;
            captured = Nodes(list.SceneNode!).SelectMany(node => node.Components.OfType<UITextComponent>()).Last();
            before = captured.Text;
            if (transferRow) captured.Transform.Parent!.Parent = other.Transform;
            else captured.Text = "Newer display";
        };
        try
        {
            list.SynchronizeVirtualRows(280);
            Assert.That(changed, Is.True);
            Assert.That(captured, Is.Not.Null);
            Assert.That(captured!.Text, Is.EqualTo(transferRow ? before : "Newer display"));
            if (transferRow) Assert.That(captured.Transform.Parent!.Parent, Is.SameAs(other.Transform));
        }
        finally
        {
            target.OnToString = null;
            detail.Destroy(true);
            other.Destroy(true);
        }
    }

    [Test]
    public void LargeReadOnlySnapshotFormatsOnlyVisibleRowsAndReusesTheirCells()
    {
        using UITestShaderScope shaders = new();
        EditorCollectionToStringTarget target = new();
        Queue values = (Queue)target.GenericValue;
        values.Clear();
        int formatted = 0;
        EditorCollectionToStringValue value = new(() => { formatted++; return "Visible value"; });
        for (int i = 0; i < 50000; i++) values.Enqueue(value);
        SceneNode detail = Create(target);
        try
        {
            UIListTransform list = Nodes(detail).Select(node => node.Transform).OfType<UIListTransform>()
                .Single(transform => transform.VirtualProvider is not null);
            Assert.That(list.VirtualProvider!.Count, Is.EqualTo(50000));
            Assert.That(formatted, Is.Zero, "Constructing the snapshot must not format offscreen values.");
            list.SynchronizeVirtualRows(280);
            Assert.That(formatted, Is.InRange(10, 15));
            int initialCalls = formatted;
            int initialNodes = Nodes(detail).Count();
            list.SynchronizeVirtualRows(280);
            Assert.That(formatted, Is.EqualTo(initialCalls));
            list.ContentScrollOffset = 280000;
            list.SynchronizeVirtualRows(280);
            Assert.That(formatted, Is.InRange(initialCalls + 10, initialCalls + 15));
            Assert.That(Nodes(detail).Count(), Is.LessThanOrEqualTo(initialNodes + 30));
            Assert.That(Nodes(detail).Count(), Is.LessThan(200));
            Assert.That(Nodes(detail).SelectMany(node => node.Components.OfType<UITextComponent>())
                .Any(label => label.Text == "Element 10000"), Is.True);
            values.Clear();
            Assert.That(list.VirtualProvider.Count, Is.EqualTo(50000), "The fallback retains its captured snapshot.");
            int beforeRetirement = formatted;
            detail.GetComponent<InspectorListDetailRefreshComponent>()!.Retire();
            Assert.That(list.VirtualProvider, Is.Null);
            list.SynchronizeVirtualRows(280);
            Assert.That(formatted, Is.EqualTo(beforeRetirement));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void EmptyReadOnlyCollectionKeepsItsEmptyLabelWithoutAVirtualProvider()
    {
        using UITestShaderScope shaders = new();
        EditorCollectionToStringTarget target = new();
        ((Queue)target.GenericValue).Clear();
        SceneNode detail = Create(target);
        try
        {
            Assert.That(Nodes(detail).Select(node => node.Transform).OfType<UIListTransform>()
                .Any(list => list.VirtualProvider is not null), Is.False);
            Assert.That(Nodes(detail).SelectMany(node => node.Components.OfType<UITextComponent>())
                .Any(label => label.Text == "<empty>"), Is.True);
        }
        finally { detail.Destroy(true); }
    }

    private static SceneNode Create(EditorCollectionToStringTarget target)
    {
        SceneNode detail = new("Read-only collection detail");
        detail.SetTransform<UIBoundableTransform>();
        var property = typeof(EditorCollectionToStringTarget).GetProperty(nameof(target.GenericValue))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(property.PropertyType, out var create), Is.True);
        create!(detail, property, [target]);
        return detail;
    }

    private static IEnumerable<SceneNode> Nodes(SceneNode root)
    {
        yield return root;
        foreach (var child in root.Transform.Children)
            if (child.SceneNode is { } node)
                foreach (SceneNode descendant in Nodes(node)) yield return descendant;
    }
}
