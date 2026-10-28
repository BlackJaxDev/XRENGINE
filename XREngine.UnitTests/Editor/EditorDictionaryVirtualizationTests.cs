using System.Reflection;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class EditorDictionaryVirtualizationTests
{
    [SetUp]
    public void ClearUndo() => Undo.ClearHistory();

    [TearDown]
    public void ClearUndoAfter() => Undo.ClearHistory();

    [Test]
    public void LargeDictionaryUsesBoundedRowsAndSetUndoAfterRecycle()
    {
        using UITestShaderScope shaders = new();
        EditorDictionaryMutationTestTarget target = new();
        for (int index = 0; index < 10000; index++)
            target.Values.Add(index.ToString("D5"), index);
        string exactKey = target.Values.Keys.ElementAt(40);
        SceneNode detail = Create(target);
        try
        {
            UIListTransform list = VirtualList(detail);
            Assert.That(list.VirtualProvider!.Count, Is.EqualTo(10000));
            list.SynchronizeVirtualRows(280);
            int initialNodes = Nodes(detail).Count();
            Assert.That(initialNodes, Is.LessThan(200));

            list.ContentScrollOffset = 28 * 40;
            list.SynchronizeVirtualRows(280);
            Assert.That(Nodes(detail).Count(), Is.LessThan(200));
            UITextInputComponent input = InputForKey(list, "00040");
            input.Binding!.SetValue("777");
            Assert.That(target.Values["00040"], Is.EqualTo(777));
            Action<UIInteractableComponent> remove = RemoveActionForIndex(detail, 40,
                out UIButtonComponent removeButton);
            remove(removeButton);
            Assert.That(target.Values.ContainsKey("00040"), Is.False);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Values.Keys.Single(key => key == "00040"), Is.SameAs(exactKey));
            Assert.That(target.Values["00040"], Is.EqualTo(777));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Values["00040"], Is.EqualTo(40));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(target.Values["00040"], Is.EqualTo(777));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(target.Values.ContainsKey("00040"), Is.False);
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void RecycledDictionaryBindingAndRemoveActionRejectTheirOldKey()
    {
        using UITestShaderScope shaders = new();
        EditorDictionaryMutationTestTarget target = new();
        for (int index = 0; index < 100; index++)
            target.Values.Add(index.ToString("D3"), index);
        SceneNode detail = Create(target);
        try
        {
            UIListTransform list = VirtualList(detail);
            list.SynchronizeVirtualRows(280);
            IUIValueBinding<string> staleBinding = InputForKey(list, "000").Binding!;
            Action<UIInteractableComponent> staleRemove = RemoveActionForIndex(detail, 0,
                out UIButtonComponent staleButton);

            list.ContentScrollOffset = 28 * 40;
            list.SynchronizeVirtualRows(280);
            Assert.Throws<InvalidOperationException>(() => staleBinding.SetValue("999"));
            staleRemove(staleButton);
            Assert.That(target.Values.Count, Is.EqualTo(100));
            Assert.That(target.Values["000"], Is.Zero);
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void PartialOwnerKeysUseVirtualReadonlyRowsAndKeepCommonEditsExact()
    {
        using UITestShaderScope shaders = new();
        EditorDictionaryMutationTestTarget first = new();
        first.Values.Add("common", 1);
        first.Values.Add("missing", 2);
        EditorDictionaryMutationTestTarget second = new();
        second.Values.Add("common", 9);
        SceneNode detail = Create(first, second);
        try
        {
            UIListTransform list = VirtualList(detail);
            Assert.That(list.VirtualProvider!.Count, Is.EqualTo(2));
            list.SynchronizeVirtualRows(280);
            SceneNode missingRow = RowForLabel(list.SceneNode!, "missing");
            Assert.That(Nodes(missingRow).SelectMany(node => node.Components.OfType<UITextInputComponent>()),
                Is.Empty);
            Assert.That(Nodes(missingRow).SelectMany(node => node.Components.OfType<UITextComponent>())
                .Any(label => label.Text == "2"), Is.True);
            Assert.That(Nodes(missingRow).SelectMany(node => node.Components.OfType<UIButtonComponent>())
                .Any(button => Nodes(button.SceneNode).SelectMany(node => node.Components.OfType<UITextComponent>())
                    .Any(label => label.Text == "Remove")), Is.False);
            UITextInputComponent common = InputForKey(list, "common");
            common.Binding!.SetValue("7");
            Assert.That(first.Values["common"], Is.EqualTo(7));
            Assert.That(second.Values["common"], Is.EqualTo(7));
            Action<UIInteractableComponent> remove = RemoveActionForIndex(detail, 0,
                out UIButtonComponent button);
            remove(button);
            Assert.That(first.Values.ContainsKey("common"), Is.False);
            Assert.That(second.Values.ContainsKey("common"), Is.False);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(first.Values["common"], Is.EqualTo(7));
            Assert.That(second.Values["common"], Is.EqualTo(7));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(first.Values["common"], Is.EqualTo(1));
            Assert.That(second.Values["common"], Is.EqualTo(9));

        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void LargePartialDictionaryKeepsPrimaryOrderAndBoundsRealizedNodes()
    {
        using UITestShaderScope shaders = new();
        EditorDictionaryMutationTestTarget first = new();
        for (int index = 0; index < 10000; index++)
            first.Values.Add(index.ToString("D5"), index);
        EditorDictionaryMutationTestTarget second = new();
        second.Values.Add("00000", 7);
        second.Values.Add("secondary-only", 8);
        SceneNode detail = Create(first, second);
        try
        {
            UIListTransform list = VirtualList(detail);
            Assert.That(list.VirtualProvider!.Count, Is.EqualTo(10000));
            list.SynchronizeVirtualRows(280);
            Assert.That(Nodes(detail).Count(), Is.LessThan(200));
            Assert.That(InputForKey(list, "00000").Binding, Is.Not.Null);
            list.ContentScrollOffset = 28 * 40;
            list.SynchronizeVirtualRows(280);
            Assert.That(Nodes(detail).Count(), Is.LessThan(200));
            SceneNode missing = RowForLabel(list.SceneNode!, "00040");
            Assert.That(Nodes(missing).SelectMany(node =>
                node.Components.OfType<UITextInputComponent>()), Is.Empty);
            Assert.That(Nodes(missing).SelectMany(node =>
                node.Components.OfType<UITextComponent>()).Any(text => text.Text == "40"), Is.True);
            list.ContentScrollOffset = 28 * 9999;
            list.SynchronizeVirtualRows(280);
            Assert.That(Nodes(detail).Count(), Is.LessThan(200));
            Assert.That(RowForLabel(list.SceneNode!, "09999"), Is.Not.Null);
            Assert.That(Nodes(detail).SelectMany(node =>
                node.Components.OfType<UITextComponent>())
                .Any(text => text.Text == "secondary-only"), Is.False);
            Assert.That(second.Values["secondary-only"], Is.EqualTo(8));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void ReusedPartialRowDetachesEditableBindingAndRemoveAction()
    {
        using UITestShaderScope shaders = new();
        EditorDictionaryMutationTestTarget first = new();
        for (int index = 0; index < 100; index++)
            first.Values.Add(index.ToString("D3"), index);
        EditorDictionaryMutationTestTarget second = new();
        second.Values.Add("000", 10);
        second.Values.Add("080", 90);
        SceneNode detail = Create(first, second);
        try
        {
            UIListTransform list = VirtualList(detail);
            list.SynchronizeVirtualRows(280);
            UITextInputComponent oldInput = InputForKey(list, "000");
            IUIValueBinding<string> oldBinding = oldInput.Binding!;
            Action<UIInteractableComponent> oldRemove = RemoveActionForIndex(detail, 0,
                out UIButtonComponent oldButton);
            object oldCell = CellStateForIndex(detail, 0);
            SceneNode oldEditor = (SceneNode)oldCell.GetType().GetProperty("EditorNode")!
                .GetValue(oldCell)!;

            list.ContentScrollOffset = 28 * 40;
            list.SynchronizeVirtualRows(280);
            SceneNode missing = RowForLabel(list.SceneNode!, "040");
            Assert.That(Nodes(missing).SelectMany(node =>
                node.Components.OfType<UITextInputComponent>()), Is.Empty);
            Assert.That(oldInput.Binding, Is.Null,
                "The previous editable binding must detach when rows become read-only.");
            Assert.That(oldEditor.IsDestroyed || !oldEditor.IsActiveSelf, Is.True);
            Assert.That(oldCell.GetType().GetProperty("RemoveAction")!.GetValue(oldCell),
                Is.Null, "The previous remove action must detach from its recycled cell.");
            Assert.That(oldButton.IsEnabled, Is.False);
            Assert.Throws<InvalidOperationException>(() => oldBinding.SetValue("999"));
            oldRemove(oldButton);
            Assert.That((first.Values["000"], second.Values["000"]),
                Is.EqualTo((0, 10)));
            Assert.That((first.Values["040"], second.Values.ContainsKey("040")),
                Is.EqualTo((40, false)));

            list.ContentScrollOffset = 28 * 80;
            list.SynchronizeVirtualRows(280);
            InputForKey(list, "080").Binding!.SetValue("123");
            Assert.That((first.Values["080"], second.Values["080"]),
                Is.EqualTo((123, 123)));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((first.Values["080"], second.Values["080"]),
                Is.EqualTo((80, 90)));
            Assert.That(Nodes(detail).Count(), Is.LessThan(200));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void MultilineMissingStringValueUsesMeasuredReadonlyRow()
    {
        using UITestShaderScope shaders = new();
        EditorStringDictionaryVirtualizationTestTarget first = new();
        first.Values.Add("common", "one");
        first.Values.Add("missing", "line one\r\nline two");
        EditorStringDictionaryVirtualizationTestTarget second = new();
        second.Values.Add("common", "two");
        SceneNode detail = CreateString(first, second);
        try
        {
            UIListTransform list = VirtualList(detail);
            list.SynchronizeVirtualRows(280);
            float twoLines = EditorVirtualRowExtent.ForLines(2, EditorUI.Styles.PropertyNameFontSize);
            Assert.That(RowExtent(list, RowForLabel(detail, "common")),
                Is.EqualTo(EditorVirtualRowExtent.SingleLineHeight));
            SceneNode missing = RowForLabel(detail, "missing");
            Assert.That(RowExtent(list, missing), Is.EqualTo(twoLines));
            Assert.That(list.VirtualContentExtent, Is.EqualTo(EditorVirtualRowExtent.SingleLineHeight + twoLines));
            Assert.That(Nodes(missing).SelectMany(node =>
                node.Components.OfType<UITextComponent>())
                .Any(text => text.Text == "line one\nline two" && text.SceneNode.IsActiveSelf), Is.True);
            Assert.That(Nodes(missing).SelectMany(node =>
                node.Components.OfType<UITextInputComponent>()).Any(input => input.SceneNode.IsActiveInHierarchy),
                Is.False);
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void MultilineStringKeyUsesMeasuredLabelRowAndWritesTheExactKey()
    {
        using UITestShaderScope shaders = new();
        EditorDictionaryMutationTestTarget target = new();
        string key = "first\r\nsecond";
        target.Values.Add(key, 4);
        target.Values.Add("single", 5);
        SceneNode detail = Create(target);
        try
        {
            UIListTransform list = VirtualList(detail);
            list.SynchronizeVirtualRows(280);
            SceneNode row = RowForLabel(detail, "first\nsecond");
            Assert.That(RowExtent(list, row),
                Is.EqualTo(EditorVirtualRowExtent.ForLines(2, EditorUI.Styles.PropertyNameFontSize)));
            Assert.That(RowExtent(list, RowForLabel(detail, "single")),
                Is.EqualTo(EditorVirtualRowExtent.SingleLineHeight));
            InputForRow(row).Binding!.SetValue("9");
            Assert.That(target.Values[key], Is.EqualTo(9));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Values[key], Is.EqualTo(4));
        }
        finally { detail.Destroy(true); }
    }

    private static float RowExtent(UIListTransform list, SceneNode row)
    {
        Assert.That(list.TryGetVirtualRowPlacement((UIBoundableTransform)row.Transform,
            out _, out _, out float extent), Is.True);
        return extent;
    }

    private static SceneNode Create(params EditorDictionaryMutationTestTarget[] targets)
    {
        SceneNode detail = new("Dictionary detail");
        detail.SetTransform<UIBoundableTransform>();
        PropertyInfo property = typeof(EditorDictionaryMutationTestTarget).GetProperty(
            nameof(EditorDictionaryMutationTestTarget.Values))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(property.PropertyType, out var create), Is.True);
        create!(detail, property, targets.Cast<object?>().ToArray());
        return detail;
    }

    private static SceneNode CreateString(params EditorStringDictionaryVirtualizationTestTarget[] targets)
    {
        SceneNode detail = new("Dictionary detail");
        detail.SetTransform<UIBoundableTransform>();
        PropertyInfo property = typeof(EditorStringDictionaryVirtualizationTestTarget).GetProperty(
            nameof(EditorStringDictionaryVirtualizationTestTarget.Values))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(property.PropertyType,
            out var create), Is.True);
        create!(detail, property, targets.Cast<object?>().ToArray());
        return detail;
    }

    private static bool HasVirtualList(SceneNode root)
        => Nodes(root).Select(node => node.Transform).OfType<UIListTransform>()
            .Any(list => list.VirtualProvider is not null);

    private static UIListTransform VirtualList(SceneNode root)
        => Nodes(root).Select(node => node.Transform).OfType<UIListTransform>()
            .Single(list => list.VirtualProvider is not null);

    private static UITextInputComponent InputForKey(UIListTransform list, string key)
        => InputForRow(RowForLabel(list.SceneNode!, key));

    private static UITextInputComponent InputForLabel(SceneNode detail, string label)
        => InputForRow(RowForLabel(detail, label));

    private static UITextInputComponent InputForRow(SceneNode row)
        => Nodes(row).SelectMany(node => node.Components.OfType<UITextInputComponent>())
            .Single(input => input.Binding is not null);

    private static SceneNode RowForLabel(SceneNode root, string label)
    {
        UITextComponent text = Nodes(root).SelectMany(node => node.Components.OfType<UITextComponent>())
            .Single(component => component.Text == label);
        return text.SceneNode.Transform.Parent!.Parent!.SceneNode!;
    }

    private static Action<UIInteractableComponent> RemoveActionForIndex(SceneNode detail, int index,
        out UIButtonComponent button)
    {
        object state = CellStateForIndex(detail, index);
        button = (UIButtonComponent)state.GetType().GetProperty("RemoveButton")!.GetValue(state)!;
        return (Action<UIInteractableComponent>)state.GetType().GetProperty("RemoveAction")!.GetValue(state)!;
    }

    private static object CellStateForIndex(SceneNode detail, int index)
    {
        InspectorListDetailRefreshComponent lifetime = detail.GetComponent<InspectorListDetailRefreshComponent>()!;
        object context = lifetime.Release!.Target!;
        System.Collections.IDictionary cells = (System.Collections.IDictionary)context.GetType()
            .GetField("_virtualCells", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
        foreach (System.Collections.DictionaryEntry entry in cells)
        {
            object state = entry.Value!;
            if ((int)state.GetType().GetProperty("Index")!.GetValue(state)! != index) continue;
            return state;
        }
        throw new AssertionException($"No realized virtual row exists for index {index}.");
    }

    private static IEnumerable<SceneNode> Nodes(SceneNode root)
    {
        if (root.IsDestroyed || root.IsDestroyQueued || !root.IsActiveSelf)
            yield break;
        yield return root;
        foreach (var child in root.Transform.Children)
            if (child.SceneNode is { } node)
                foreach (SceneNode descendant in Nodes(node)) yield return descendant;
    }
}
