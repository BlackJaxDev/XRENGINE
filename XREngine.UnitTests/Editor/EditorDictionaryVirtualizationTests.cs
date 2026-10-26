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
    public void MissingOwnerKeysKeepCurrentEagerReadonlyBehavior()
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
            Assert.That(HasVirtualList(detail), Is.False,
                "A partial-key selection keeps the existing eager row presentation.");
            UITextInputComponent common = InputForLabel(detail, "common");
            common.Binding!.SetValue("7");
            Assert.That(first.Values["common"], Is.EqualTo(7));
            Assert.That(second.Values["common"], Is.EqualTo(7));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(first.Values["common"], Is.EqualTo(1));
            Assert.That(second.Values["common"], Is.EqualTo(9));

            SceneNode missingRow = RowForLabel(detail, "missing");
            Assert.That(Nodes(missingRow).SelectMany(node => node.Components.OfType<UITextInputComponent>()),
                Is.Empty);
            Assert.That(Nodes(missingRow).SelectMany(node => node.Components.OfType<UITextComponent>())
                .Any(label => label.Text == "2"), Is.True);
            Assert.That(Nodes(missingRow).SelectMany(node => node.Components.OfType<UIButtonComponent>())
                .Any(button => Nodes(button.SceneNode).SelectMany(node => node.Components.OfType<UITextComponent>())
                    .Any(label => label.Text == "Remove")), Is.False);
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void MultilineStringKeyKeepsEagerLabelLayout()
    {
        using UITestShaderScope shaders = new();
        EditorDictionaryMutationTestTarget target = new();
        string key = "first\nsecond";
        target.Values.Add(key, 4);
        SceneNode detail = Create(target);
        try
        {
            Assert.That(HasVirtualList(detail), Is.False);
            Assert.That(Nodes(detail).SelectMany(node => node.Components.OfType<UITextComponent>())
                .Any(label => label.Text == key), Is.True);
        }
        finally { detail.Destroy(true); }
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
        InspectorListDetailRefreshComponent lifetime = detail.GetComponent<InspectorListDetailRefreshComponent>()!;
        object context = lifetime.Release!.Target!;
        System.Collections.IDictionary cells = (System.Collections.IDictionary)context.GetType()
            .GetField("_virtualCells", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
        foreach (System.Collections.DictionaryEntry entry in cells)
        {
            object state = entry.Value!;
            if ((int)state.GetType().GetProperty("Index")!.GetValue(state)! != index) continue;
            button = (UIButtonComponent)state.GetType().GetProperty("RemoveButton")!.GetValue(state)!;
            return (Action<UIInteractableComponent>)state.GetType().GetProperty("RemoveAction")!.GetValue(state)!;
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
