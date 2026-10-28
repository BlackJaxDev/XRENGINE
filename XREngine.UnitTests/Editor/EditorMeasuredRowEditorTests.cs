using System.Reflection;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class EditorMeasuredRowEditorTests
{
    [SetUp]
    public void ClearUndo() => Undo.ClearHistory();

    [TearDown]
    public void ClearUndoAfter() => Undo.ClearHistory();

    [Test]
    public void MeasuredCustomListRowsUseTheEditorHeights()
    {
        using UITestShaderScope shaders = new();
        MeasuredListTarget target = new() { Values = [new(40), new(90), new(10), new(float.NaN)] };
        SceneNode detail = Create(target, nameof(MeasuredListTarget.Values));
        try
        {
            UIListTransform list = VirtualList(detail);
            list.SynchronizeVirtualRows(400);
            IVirtualListItemExtents extents = (IVirtualListItemExtents)list.VirtualProvider!;
            float single = EditorVirtualRowExtent.SingleLineHeight;
            Assert.Multiple(() =>
            {
                Assert.That(extents.HasItemExtents, Is.True);
                Assert.That(extents.GetItemExtent(0), Is.EqualTo(40));
                Assert.That(extents.GetItemExtent(1), Is.EqualTo(90));
                Assert.That(extents.GetItemExtent(2), Is.EqualTo(single));
                Assert.That(extents.GetItemExtent(3), Is.EqualTo(single));
                Assert.That(list.VirtualContentExtent, Is.EqualTo(40 + 90 + single + single));
                Assert.That(Nodes(detail).Count(node => node.Name == MeasuredEditorAttribute.EditorName),
                    Is.EqualTo(4));
            });
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void MeasuredCustomListRowsReadNewHeightsAfterRefresh()
    {
        using UITestShaderScope shaders = new();
        MeasuredListTarget target = new() { Values = [new(40)] };
        SceneNode detail = Create(target, nameof(MeasuredListTarget.Values));
        try
        {
            UIListTransform list = VirtualList(detail);
            list.SynchronizeVirtualRows(400);
            IVirtualListItemExtents extents = (IVirtualListItemExtents)list.VirtualProvider!;
            ulong revision = extents.ExtentRevision;
            target.Values[0] = new(120);
            object context = detail.GetComponent<InspectorListDetailRefreshComponent>()!.Release!.Target!;
            context.GetType().GetMethod("RefreshList")!.Invoke(context, null);
            list.SynchronizeVirtualRows(400);

            Assert.That(extents.ExtentRevision, Is.Not.EqualTo(revision));
            Assert.That(list.VirtualContentExtent, Is.EqualTo(120));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void MeasuredCustomDictionaryRowsUseTheLargerHeight()
    {
        using UITestShaderScope shaders = new();
        MeasuredDictionaryTarget target = new();
        target.Values.Add("tall", new(100));
        target.Values.Add("first\nsecond\nthird", new(30));
        SceneNode detail = Create(target, nameof(MeasuredDictionaryTarget.Values));
        try
        {
            UIListTransform list = VirtualList(detail);
            list.SynchronizeVirtualRows(400);
            IVirtualListItemExtents extents = (IVirtualListItemExtents)list.VirtualProvider!;
            float threeLines = EditorVirtualRowExtent.ForLines(3, EditorUI.Styles.PropertyNameFontSize);
            Assert.Multiple(() =>
            {
                Assert.That(extents.GetItemExtent(0), Is.EqualTo(100));
                Assert.That(extents.GetItemExtent(1), Is.EqualTo(MathF.Max(30, threeLines)));
            });
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void UnmeasuredCustomDictionaryValuesKeepTheFallback()
    {
        using UITestShaderScope shaders = new();
        UnmeasuredDictionaryTarget target = new();
        target.Values.Add("only", new());
        SceneNode detail = Create(target, nameof(UnmeasuredDictionaryTarget.Values));
        try
        {
            Assert.That(Nodes(detail).Select(node => node.Transform).OfType<UIListTransform>()
                .Any(list => list.VirtualProvider is not null), Is.False);
            Assert.That(Nodes(detail).Any(node => node.Name == UnmeasuredEditorAttribute.EditorName), Is.True);
        }
        finally { detail.Destroy(true); }
    }

    private static SceneNode Create(object target, string propertyName)
    {
        SceneNode detail = new("Measured row detail");
        detail.SetTransform<UIBoundableTransform>();
        PropertyInfo property = target.GetType().GetProperty(propertyName)!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(property.PropertyType,
            out var create), Is.True);
        create!(detail, property, [target]);
        return detail;
    }

    private static UIListTransform VirtualList(SceneNode root)
        => Nodes(root).Select(node => node.Transform).OfType<UIListTransform>()
            .Single(list => list.VirtualProvider is not null);

    private static IEnumerable<SceneNode> Nodes(SceneNode root)
    {
        if (root.IsDestroyed || root.IsDestroyQueued || !root.IsActiveSelf)
            yield break;
        yield return root;
        foreach (var child in root.Transform.Children)
            if (child.SceneNode is { } node)
                foreach (SceneNode descendant in Nodes(node)) yield return descendant;
    }

    [AttributeUsage(AttributeTargets.Class)]
    private sealed class MeasuredEditorAttribute : EditorComponentAttribute, IEditorMeasuredRowEditor
    {
        public const string EditorName = "Measured test editor";

        public override void CreateEditor(SceneNode node, PropertyInfo prop, object?[]? objects)
            => node.NewChild().Name = EditorName;

        public float MeasureRowHeight(object? value)
            => value is MeasuredValue measured ? measured.Height : 0;
    }

    [AttributeUsage(AttributeTargets.Class)]
    private sealed class UnmeasuredEditorAttribute : EditorComponentAttribute
    {
        public const string EditorName = "Unmeasured test editor";

        public override void CreateEditor(SceneNode node, PropertyInfo prop, object?[]? objects)
            => node.NewChild().Name = EditorName;
    }

    [MeasuredEditor]
    private sealed class MeasuredValue(float height)
    {
        public float Height { get; } = height;
    }

    [UnmeasuredEditor]
    private sealed class UnmeasuredValue;

    private sealed class MeasuredListTarget
    {
        public List<MeasuredValue> Values { get; set; } = [];
    }

    private sealed class MeasuredDictionaryTarget
    {
        public Dictionary<string, MeasuredValue> Values { get; set; } = [];
    }

    private sealed class UnmeasuredDictionaryTarget
    {
        public Dictionary<string, UnmeasuredValue> Values { get; set; } = [];
    }
}
