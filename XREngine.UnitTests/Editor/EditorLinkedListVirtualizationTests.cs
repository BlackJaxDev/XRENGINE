using System.Reflection;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Rendering;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class EditorLinkedListVirtualizationTests
{
    [SetUp]
    public void ClearUndo() => Undo.ClearHistory();

    [TearDown]
    public void ClearUndoAfter()
    {
        Undo.ClearHistory();
        // A bound property write queues a deferred editor refresh. The test host does not run the
        // scene-edit queue, so remove the refresh of the destroyed detail before the next fixture.
        ((System.Collections.Concurrent.ConcurrentQueue<Action>)typeof(EditorImGuiUI)
            .GetField("_queuedSceneEdits", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Clear();
    }

    [Test]
    public void LargeIntegerListUsesBoundedRowsAndKeepsSetUndoAndRemoveHistoryAfterRecycle()
    {
        using UITestShaderScope shaders = new();
        EditorLinkedMutationTestTarget target = new() { Values = new(Enumerable.Range(0, 10000)) };
        SceneNode detail = Create(target, nameof(target.Values));
        try
        {
            UIListTransform list = VirtualList(detail);
            Assert.That(list.VirtualProvider!.Count, Is.EqualTo(10000));
            list.SynchronizeVirtualRows(280);
            int initialNodeCount = Nodes(detail).Count();
            Assert.That(initialNodeCount, Is.LessThan(200));

            UITextInputComponent firstInput = InputForRow(list, 0);
            IUIValueBinding<string> staleBinding = firstInput.Binding!;
            Action<UIInteractableComponent> staleRemove = RemoveActionForIndex(detail, 0,
                out UIButtonComponent staleRemoveButton);
            list.ContentScrollOffset = 28 * 40;
            list.SynchronizeVirtualRows(280);
            Assert.That(Nodes(detail).Count(), Is.LessThan(200));
            Assert.Throws<InvalidOperationException>(() => staleBinding.SetValue("999"));
            staleRemove(staleRemoveButton);
            Assert.That(target.Values.Count, Is.EqualTo(10000), "A retired Remove callback must not mutate its old node.");
            Assert.That(target.Values.First!.Value, Is.Zero);

            UITextInputComponent recycledInput = InputForRow(list, 40);
            recycledInput.Binding!.SetValue("777");
            LinkedListNode<int> editedNode = target.Values.First;
            for (int i = 0; i < 40; i++) editedNode = editedNode!.Next!;
            Assert.That(editedNode!.Value, Is.EqualTo(777));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(editedNode.Value, Is.EqualTo(40));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(editedNode.Value, Is.EqualTo(777));

            list = VirtualList(detail);
            list.ContentScrollOffset = 28 * 40;
            list.SynchronizeVirtualRows(280);
            Action<UIInteractableComponent> remove = RemoveActionForIndex(detail, 40, out UIButtonComponent button);
            remove(button);
            Assert.That(editedNode.List, Is.Null, "A Remove action created after Set must use the committed source snapshot.");
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Values.First!.Next!.Next, Is.Not.Null);
            Assert.That(target.Values.Find(777), Is.SameAs(editedNode));
            Assert.That(editedNode.Value, Is.EqualTo(777));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void TransferringValueHostOrRecyclingRowRejectsItsRetainedBinding()
    {
        using UITestShaderScope shaders = new();
        EditorLinkedMutationTestTarget target = new() { Values = new(Enumerable.Range(0, 100)) };
        SceneNode detail = Create(target, nameof(target.Values));
        SceneNode foreign = new("Foreign value host");
        foreign.SetTransform<UIBoundableTransform>();
        SceneNode originalValueParent;
        try
        {
            UIListTransform list = VirtualList(detail);
            list.SynchronizeVirtualRows(280);
            UITextInputComponent input = InputForRow(list, 0);
            IUIValueBinding<string> binding = input.Binding!;
            SceneNode valueHost = input.SceneNode.Transform.Parent!.Parent!.SceneNode!;
            originalValueParent = valueHost.Transform.Parent!.SceneNode!;
            valueHost.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
            Assert.Throws<InvalidOperationException>(() => binding.SetValue("500"));
            Assert.That(target.Values.First!.Value, Is.Zero);
            valueHost.Transform.SetParent(originalValueParent.Transform, false,
                EParentAssignmentMode.Immediate);

            list.ContentScrollOffset = 28 * 40;
            list.SynchronizeVirtualRows(280);
            Assert.Throws<InvalidOperationException>(() => binding.SetValue("600"));
            Assert.That(target.Values.First!.Value, Is.Zero);
        }
        finally
        {
            detail.Destroy(true);
            foreign.Destroy(true);
        }
    }

    [Test]
    public void VariableHeightAnimationEditorRetainsEagerLayout()
    {
        using UITestShaderScope shaders = new();
        LinkedAnimationTarget target = new()
        {
            Values = new([new PropAnimFloat(2, false, true)])
        };
        SceneNode detail = Create(target, nameof(target.Values));
        try
        {
            InspectorListDetailRefreshComponent lifetime = detail.GetComponent<InspectorListDetailRefreshComponent>()!;
            object context = lifetime.Release!.Target!;
            var cells = (System.Collections.IDictionary)context.GetType().GetField("_virtualCells",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
            Assert.That(cells.Count, Is.Zero, "The variable-height animation editor must stay on its eager route.");
            Assert.That(Nodes(detail).Select(node => node.Transform).OfType<UIScrollableTransform>()
                .Any(scroll => scroll.Height == 240), Is.True);
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void VectorEditorKeepsItsNestedBindingsAfterRecycle()
    {
        using UITestShaderScope shaders = new();
        LinkedVectorTarget target = new()
        {
            Values = new(Enumerable.Range(0, 100).Select(index => new System.Numerics.Vector2(index, -index)))
        };
        SceneNode detail = Create(target, nameof(target.Values));
        detail.SetWorldContext(new TestWorldContext());
        try
        {
            UIListTransform list = VirtualList(detail);
            list.SynchronizeVirtualRows(280);
            list.ContentScrollOffset = 28 * 40;
            list.SynchronizeVirtualRows(280);
            list.ContentScrollOffset = 0;
            list.SynchronizeVirtualRows(280);
            UITextInputComponent xInput = Nodes(RowForIndex(list, 0))
                .SelectMany(node => node.Components.OfType<UITextInputComponent>())
                .Single(input => input.SceneNode.Transform.Parent?.SceneNode?.Name == "X");
            xInput.Binding!.SetValue("321");
            Assert.That(target.Values.First!.Value.X, Is.EqualTo(321));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void NestedInputTransferRejectsWriteAndIsNotRecapturedOnNextBind()
    {
        using UITestShaderScope shaders = new();
        EditorLinkedMutationTestTarget target = new() { Values = new(Enumerable.Range(0, 100)) };
        SceneNode detail = Create(target, nameof(target.Values));
        SceneNode foreign = new("Transferred linked editor");
        foreign.SetTransform<UIBoundableTransform>();
        try
        {
            UIListTransform list = VirtualList(detail);
            list.SynchronizeVirtualRows(280);
            UITextInputComponent input = InputForRow(list, 0);
            SceneNode inputNode = input.SceneNode;
            SceneNode originalParent = inputNode.Transform.Parent!.SceneNode!;
            IUIValueBinding<string> oldBinding = input.Binding!;
            inputNode.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
            Assert.Throws<InvalidOperationException>(() => oldBinding.SetValue("999"));
            Assert.That(target.Values.First!.Value, Is.Zero);
            inputNode.Transform.SetParent(originalParent.Transform, false, EParentAssignmentMode.Immediate);

            bool transferred = false;
            input.PropertyChanging += (_, change) =>
            {
                if (transferred || change.PropertyName != nameof(UITextInputComponent.Binding)) return;
                transferred = true;
                inputNode.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
            };
            list.ContentScrollOffset = 28 * 40;
            list.SynchronizeVirtualRows(280);
            Assert.That(transferred, Is.True);
            Assert.That(inputNode.Transform.Parent, Is.SameAs(foreign.Transform));
            Assert.That(input.Binding, Is.Null);
            SceneNode rowEditor = originalParent;
            Assert.That(Nodes(rowEditor).SelectMany(node => node.Components.OfType<UITextInputComponent>()),
                Is.Empty, "The recycled editor must not adopt the transferred input node.");
        }
        finally
        {
            detail.Destroy(true);
            foreign.Destroy(true);
        }
    }

    [Test]
    public void ControlTransformRegenerationRejectsItsRetainedBinding()
    {
        using UITestShaderScope shaders = new();
        EditorLinkedMutationTestTarget target = new() { Values = new([1, 2]) };
        SceneNode detail = Create(target, nameof(target.Values));
        try
        {
            UIListTransform list = VirtualList(detail);
            list.SynchronizeVirtualRows(280);
            UITextInputComponent input = InputForRow(list, 0);
            IUIValueBinding<string> binding = input.Binding!;
            input.Transform.Generate();
            Assert.Throws<InvalidOperationException>(() => binding.SetValue("999"));
            Assert.That(target.Values.First!.Value, Is.EqualTo(1));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void InitialControlTransferCannotBecomeAnEmptyAcceptedEditor()
    {
        using UITestShaderScope shaders = new();
        EditorLinkedMutationTestTarget target = new() { Values = new([1, 2]) };
        SceneNode detail = Create(target, nameof(target.Values));
        SceneNode foreign = new("Transferred initial input");
        foreign.SetTransform<UIBoundableTransform>();
        UITextInputComponent? transferred = null;
        IUIValueBinding<string>? retainedBinding = null;
        HashSet<SceneNode> observedNodes = [];
        HashSet<object> observedTransforms = [];
        void ObserveChildren(SceneNode node)
        {
            if (!observedTransforms.Add(node.Transform)) return;
            node.Transform.Children.PostAnythingAdded += child =>
            {
                if (child.SceneNode is { } added) Observe(added);
            };
            foreach (var child in node.Transform.Children)
                if (child.SceneNode is { } existing) Observe(existing);
        }
        void Observe(SceneNode node)
        {
            if (!observedNodes.Add(node)) return;
            node.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(SceneNode.Transform)) ObserveChildren(node);
            };
            node.ComponentAdded += pair =>
            {
                if (pair.comp is not UITextInputComponent input) return;
                input.PropertyChanged += (_, change) =>
                {
                    if (transferred is not null || change.PropertyName != nameof(input.Binding) ||
                        input.Binding is not { } binding) return;
                    transferred = input;
                    retainedBinding = binding;
                    input.SceneNode.Transform.SetParent(foreign.Transform, false,
                        EParentAssignmentMode.Immediate);
                };
            };
            ObserveChildren(node);
        }
        try
        {
            UIListTransform list = VirtualList(detail);
            Observe(list.SceneNode!);
            Exception? error = Assert.Catch(() => list.SynchronizeVirtualRows(280));
            Assert.That(error!.ToString(), Does.Contain("lost a control during creation"));
            Assert.That(transferred, Is.Not.Null);
            Assert.That(transferred!.SceneNode.Parent, Is.SameAs(foreign));
            Assert.That(retainedBinding, Is.Not.Null);
            Assert.Throws<InvalidOperationException>(() => retainedBinding!.SetValue("999"));
            Assert.That(target.Values.First!.Value, Is.EqualTo(1));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally
        {
            detail.Destroy(true);
            foreign.Destroy(true);
        }
    }

    private static SceneNode Create(object target, string propertyName)
    {
        SceneNode detail = new("Linked collection detail");
        detail.SetTransform<UIBoundableTransform>();
        PropertyInfo property = target.GetType().GetProperty(propertyName)!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(property.PropertyType, out var create), Is.True);
        create!(detail, property, [target]);
        return detail;
    }

    private static UIListTransform VirtualList(SceneNode root)
        => Nodes(root).Select(node => node.Transform).OfType<UIListTransform>()
            .Single(list => list.VirtualProvider is not null);

    private static UITextInputComponent InputForRow(UIListTransform list, int index)
    {
        UITextComponent label = Nodes(list.SceneNode!).SelectMany(node => node.Components.OfType<UITextComponent>())
            .Single(text => text.Text == $"Node {index}");
        SceneNode row = label.SceneNode.Transform.Parent!.Parent!.SceneNode!;
        return Nodes(row).SelectMany(node => node.Components.OfType<UITextInputComponent>())
            .Single(input => input.Binding is not null);
    }

    private static SceneNode RowForIndex(UIListTransform list, int index)
    {
        UITextComponent label = Nodes(list.SceneNode!).SelectMany(node => node.Components.OfType<UITextComponent>())
            .Single(text => text.Text == $"Node {index}");
        return label.SceneNode.Transform.Parent!.Parent!.SceneNode!;
    }

    private static Action<UIInteractableComponent> RemoveActionForIndex(SceneNode detail, int index,
        out UIButtonComponent button)
    {
        InspectorListDetailRefreshComponent lifetime = detail.GetComponent<InspectorListDetailRefreshComponent>()!;
        object context = lifetime.Release!.Target!;
        FieldInfo cellsField = context.GetType().GetField("_virtualCells",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        System.Collections.IDictionary cells = (System.Collections.IDictionary)cellsField.GetValue(context)!;
        foreach (System.Collections.DictionaryEntry entry in cells)
        {
            object state = entry.Value!;
            if ((int)state.GetType().GetProperty("Index")!.GetValue(state)! != index) continue;
            button = (UIButtonComponent)state.GetType().GetProperty("RemoveButton")!.GetValue(state)!;
            return (Action<UIInteractableComponent>)state.GetType().GetProperty("RemoveAction")!.GetValue(state)!;
        }
        throw new AssertionException($"No live linked row exists for index {index}.");
    }

    private static IEnumerable<SceneNode> Nodes(SceneNode root)
    {
        yield return root;
        foreach (var child in root.Transform.Children)
            if (child.SceneNode is { } node)
                foreach (SceneNode descendant in Nodes(node)) yield return descendant;
    }

    private sealed class LinkedAnimationTarget
    {
        public LinkedList<PropAnimFloat> Values { get; set; } = new();
    }

    private sealed class LinkedVectorTarget
    {
        public LinkedList<System.Numerics.Vector2> Values { get; set; } = new();
    }
}
