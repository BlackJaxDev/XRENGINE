using System.Collections;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed partial class EditorNestedListDetailTests
{
    [SetUp]
    public void ClearUndo() => Undo.ClearHistory();

    [TearDown]
    public void ClearUndoAfter() => Undo.ClearHistory();

    [Test]
    public void NestedSetUndoRedoSurvivesOuterListRowRecycle()
    {
        using UITestShaderScope shaders = new();
        EditorNestedListTarget target = new()
        {
            Values = Enumerable.Range(0, 80).Select(index => new List<int> { index, index + 1 }).ToList()
        };
        SceneNode detail = Create(target);
        try
        {
            UIListTransform outer = VirtualList(detail, 80);
            outer.SynchronizeVirtualRows(280);
            SceneNode nestedRoot = OpenNestedDetail(detail, outer, 0);
            UIListTransform inner = VirtualList(nestedRoot, 2);
            inner.SynchronizeVirtualRows(280);

            InputForRow(inner, 0).Binding!.SetValue("91");
            Assert.That(target.Values[0][0], Is.EqualTo(91));
            inner.SynchronizeVirtualRows(280);

            outer.ContentScrollOffset = 28 * 40;
            outer.SynchronizeVirtualRows(280);
            InputForRow(inner, 0).Binding!.SetValue("92");
            Assert.That(target.Values[0][0], Is.EqualTo(92));

            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Values[0][0], Is.EqualTo(91));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(target.Values[0][0], Is.EqualTo(92));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void RebindTransferPreservesAdoptedNestedEditor()
    {
        using UITestShaderScope shaders = new();
        EditorNestedListTarget target = new()
        {
            Values = [Enumerable.Range(0, 80).ToList()]
        };
        SceneNode detail = Create(target);
        SceneNode foreign = new("Foreign nested editor owner");
        foreign.SetTransform<UIBoundableTransform>();
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode nestedRoot = OpenNestedDetail(detail, outer, 0);
            UIListTransform inner = VirtualList(nestedRoot, 80);
            inner.SynchronizeVirtualRows(280);

            object state = RowStateForIndex(nestedRoot, 0);
            SceneNode editor = (SceneNode)state.GetType().GetProperty("EditorNode")!.GetValue(state)!;
            UITextInputComponent input = InputForRow(inner, 0);
            SceneNode inputNode = input.SceneNode;
            input.IsReadOnly = true;
            SceneNode originalEditorParent = editor.Transform.Parent!.SceneNode!;
            bool transferred = false;
            input.PropertyChanging += (_, change) =>
            {
                if (transferred || change.PropertyName != nameof(UITextInputComponent.Binding)) return;
                transferred = true;
                editor.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
            };

            inner.ContentScrollOffset = 28 * 40;
            inner.SynchronizeVirtualRows(280);

            Assert.Multiple(() =>
            {
                Assert.That(transferred, Is.True);
                Assert.That(editor.Transform.Parent, Is.SameAs(foreign.Transform));
                Assert.That(editor.IsDestroyed || editor.IsDestroyQueued, Is.False);
                Assert.That(editor.IsActiveSelf, Is.True);
                Assert.That(input.SceneNode, Is.SameAs(inputNode));
                Assert.That(input.IsReadOnly, Is.True);
                Assert.That(originalEditorParent.IsDestroyed || originalEditorParent.IsDestroyQueued, Is.False);
            });
        }
        finally
        {
            detail.Destroy(true);
            foreign.Destroy(true);
        }
    }

    [Test]
    public void NestedScalarRetainsReceiptWhenTransferredComponentCannotBeAborted()
    {
        using UITestShaderScope shaders = new();
        EditorNestedListTarget target = new() { Values = [[1]] };
        SceneNode detail = Create(target);
        SceneNode foreign = new("Foreign nested component owner");
        foreign.SetTransform<UIBoundableTransform>();
        SceneNode? transferredNode = null;
        bool transferred = false;
        void Adopt(XRComponent component)
        {
            if (transferred || component is not UIIntInputComponent || component.SceneNode is not { } node)
                return;
            transferred = true;
            transferredNode = node;
            node.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
        }
        XRComponent.ComponentCreated += Adopt;
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode nestedRoot = OpenNestedDetail(detail, outer, 0);
            UIListTransform inner = VirtualList(nestedRoot, 1);
            inner.SynchronizeVirtualRows(280);

            object context = Nodes(nestedRoot).SelectMany(node =>
                node.Components.OfType<InspectorListDetailRefreshComponent>()).Single().Release!.Target!;
            IList retained = (IList)context.GetType().GetField("_failedComponentCreations",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
            Assert.Multiple(() =>
            {
                Assert.That(transferred, Is.True);
                Assert.That(transferredNode, Is.Not.Null);
                Assert.That(transferredNode!.Transform.Parent, Is.SameAs(foreign.Transform));
                Assert.That(transferredNode.IsDestroyed || transferredNode.IsDestroyQueued, Is.False);
                Assert.That(retained.Count, Is.GreaterThan(0));
            });
        }
        finally
        {
            XRComponent.ComponentCreated -= Adopt;
            detail.Destroy(true);
            foreign.Destroy(true);
        }
    }

    [Test]
    public void OuterCollectionRetainsFailedNestedDetailLifetimeReceipt()
    {
        using UITestShaderScope shaders = new();
        EditorNestedListTarget target = new() { Values = [[1]] };
        SceneNode detail = Create(target);
        SceneNode foreign = new("Foreign nested lifetime owner");
        foreign.SetTransform<UIBoundableTransform>();
        InspectorListDetailRefreshComponent? transferredLifetime = null;
        SceneNode? transferredNode = null;
        void Adopt(XRComponent component)
        {
            if (component is not InspectorListDetailRefreshComponent lifetime ||
                component.SceneNode?.Transform.Parent?.SceneNode?.Name != "NestedListDetail")
                return;
            transferredLifetime = lifetime;
            transferredNode = component.SceneNode;
            transferredNode.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
        }
        XRComponent.ComponentCreated += Adopt;
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            InvokeNestedAction(detail, 0);

            object outerContext = detail.GetComponent<InspectorListDetailRefreshComponent>()!.Release!.Target!;
            IList retained = (IList)outerContext.GetType().GetField("_failedComponentCreations",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(outerContext)!;
            object retainedReceipt = retained.Cast<object>().Single(item =>
                ReferenceEquals(item.GetType().GetProperty("Component",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item), transferredLifetime));

            Assert.Multiple(() =>
            {
                Assert.That(transferredLifetime, Is.Not.Null);
                Assert.That(transferredNode, Is.Not.Null);
                Assert.That(transferredNode!.Transform.Parent, Is.SameAs(foreign.Transform));
                Assert.That(transferredNode.IsDestroyed || transferredNode.IsDestroyQueued, Is.False);
                Assert.That(retainedReceipt, Is.Not.Null);
            });
        }
        finally
        {
            XRComponent.ComponentCreated -= Adopt;
            detail.Destroy(true);
            foreign.Destroy(true);
        }
    }

    [Test]
    public void OrdinaryStringCollectionKeepsItsReusableEditorPath()
    {
        using UITestShaderScope shaders = new();
        EditorOrdinaryStringListTarget target = new()
        {
            Values = Enumerable.Range(0, 80).Select(index => $"value {index}").ToList()
        };
        SceneNode detail = new("Ordinary string collection detail");
        detail.SetTransform<UIBoundableTransform>();
        PropertyInfo property = typeof(EditorOrdinaryStringListTarget).GetProperty(nameof(EditorOrdinaryStringListTarget.Values))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(property.PropertyType, out var create), Is.True);
        create!(detail, property, [target]);
        try
        {
            UIListTransform list = VirtualList(detail, 80);
            list.SynchronizeVirtualRows(280);
            // Row reuse follows the list's LIFO recycle order: old row 9 becomes row 40 here.
            UITextInputComponent input = InputForRow(list, 9);
            list.ContentScrollOffset = 28 * 40;
            list.SynchronizeVirtualRows(280);
            UITextInputComponent recycledInput = InputForRow(list, 40);
            Assert.That(recycledInput, Is.SameAs(input));
            recycledInput.Binding!.SetValue("updated ordinary value");
            Assert.That(target.Values[40], Is.EqualTo("updated ordinary value"));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void RecycledNestedEnumOpeningUsesCurrentEditorOwner()
    {
        using UITestShaderScope shaders = new();
        SceneNode canvas = new("Nested enum canvas", new UICanvasTransform());
        canvas.SetWorldContext(new TestWorldContext());
        canvas.AddComponent<UICanvasComponent>();
        canvas.AddComponent<UICanvasInputComponent>();
        EditorNestedEnumTarget target = new()
        {
            Values = [Enumerable.Range(0, 80).Select(index => (EditorNestedEnum)(index % 3)).ToList()]
        };
        SceneNode detail = new("Nested enum detail");
        detail.SetTransform<UIBoundableTransform>();
        detail.Transform.SetParent(canvas.Transform, false, EParentAssignmentMode.Immediate);
        PropertyInfo property = typeof(EditorNestedEnumTarget).GetProperty(nameof(EditorNestedEnumTarget.Values))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(property.PropertyType, out var create), Is.True);
        create!(detail, property, [target]);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode nested = OpenNestedDetail(detail, outer, 0);
            UIListTransform inner = VirtualList(nested, 80);
            inner.SynchronizeVirtualRows(280);
            // Row reuse follows the list's LIFO recycle order: old row 9 becomes row 40 here.
            object reusedState = RowStateForIndex(nested, 9);
            SceneNode editorNode = (SceneNode)reusedState.GetType().GetProperty("EditorNode")!.GetValue(reusedState)!;
            UIComboBoxComponent combo = Nodes(editorNode).SelectMany(node => node.Components.OfType<UIComboBoxComponent>()).Single();
            NativePropertyPopupHost popupHost = Nodes(editorNode)
                .SelectMany(node => node.Components.OfType<NativePropertyPopupHost>()).Single();
            InspectorEnumEditorComponent enumEditor = Nodes(editorNode)
                .SelectMany(node => node.Components.OfType<InspectorEnumEditorComponent>()).Single();

            inner.ContentScrollOffset = 28 * 40;
            inner.SynchronizeVirtualRows(280);
            object recycledState = RowStateForIndex(nested, 40);
            SceneNode recycledEditor = (SceneNode)recycledState.GetType().GetProperty("EditorNode")!.GetValue(recycledState)!;
            Assert.That(Nodes(recycledEditor).SelectMany(node => node.Components.OfType<UIComboBoxComponent>()).Single(), Is.SameAs(combo));
            Assert.That(enumEditor.IsOwnerCurrent, Is.True);
            popupHost.UITransform.Translation = new Vector2(23.0f, 41.0f);
            Vector2 expected = popupHost.GetPosition();
            Delegate opening = (Delegate)typeof(UIComboBoxComponent)
                .GetField("Opening", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(combo)!;
            opening.DynamicInvoke(combo);
            Assert.That(combo.PopupPosition, Is.EqualTo(expected));
        }
        finally { canvas.Destroy(true); }
    }

    [Test]
    public void GuardedPopupCreationPreservesPopupAdoptedDuringComponentCreation()
    {
        using UITestShaderScope shaders = new();
        SceneNode canvas = new("Popup ownership canvas", new UICanvasTransform());
        canvas.SetWorldContext(new TestWorldContext());
        canvas.AddComponent<UICanvasComponent>();
        SceneNode hostNode = new(canvas, "Popup host");
        hostNode.SetTransform<UIBoundableTransform>();
        NativePropertyPopupHost host = hostNode.AddComponent<NativePropertyPopupHost>()!;
        SceneNode foreign = new("Foreign popup owner");
        foreign.SetTransform<UIBoundableTransform>();
        SceneNode? adoptedNode = null;
        UIContextMenuComponent? adoptedPopup = null;
        void Adopt(XRComponent component)
        {
            if (component is not UIContextMenuComponent popup) return;
            adoptedPopup = popup;
            adoptedNode = popup.SceneNode;
            popup.SceneNode.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
        }
        XRComponent.ComponentCreated += Adopt;
        bool created;
        UIContextMenuComponent? popup;
        try { created = host.TryCreatePopup(static () => true, out popup); }
        finally { XRComponent.ComponentCreated -= Adopt; }
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(created, Is.False);
                Assert.That(popup, Is.Null);
                Assert.That(adoptedNode, Is.Not.Null);
                Assert.That(adoptedPopup, Is.Not.Null);
                Assert.That(adoptedNode!.IsDestroyed || adoptedNode.IsDestroyQueued, Is.False);
                Assert.That(adoptedNode.Transform.Parent, Is.SameAs(foreign.Transform));
                Assert.That(host.OwnsPopup(adoptedPopup!), Is.False);
            });
        }
        finally
        {
            canvas.Destroy(true);
            foreign.Destroy(true);
        }
    }

    [Test]
    public void GuardedPopupCreationCleansUpAfterThrowingComponentCreatedCallback()
    {
        using UITestShaderScope shaders = new();
        SceneNode canvas = new("Throwing popup canvas", new UICanvasTransform());
        canvas.SetWorldContext(new TestWorldContext());
        canvas.AddComponent<UICanvasComponent>();
        SceneNode hostNode = new(canvas, "Popup host");
        hostNode.SetTransform<UIBoundableTransform>();
        NativePropertyPopupHost host = hostNode.AddComponent<NativePropertyPopupHost>()!;
        void Throw(XRComponent component)
        {
            if (component is UIContextMenuComponent)
                throw new InvalidOperationException("fixture component creation failure");
        }
        XRComponent.ComponentCreated += Throw;
        try
        {
            Assert.Throws<InvalidOperationException>(() => host.TryCreatePopup(static () => true, out _));
        }
        finally { XRComponent.ComponentCreated -= Throw; }
        try
        {
            Assert.That(Nodes(canvas).Any(node => node.Name == "Property popup" &&
                !node.IsDestroyed && !node.IsDestroyQueued), Is.False);
        }
        finally { canvas.Destroy(true); }
    }

    [Test]
    public void NestedFlagsEnumRowsRemainUnavailableToFixedHeightEditor()
    {
        using UITestShaderScope shaders = new();
        EditorNestedFlagsEnumTarget target = new() { Values = [[EditorNestedFlagsEnum.First]] };
        SceneNode detail = new("Nested flags enum detail");
        detail.SetTransform<UIBoundableTransform>();
        PropertyInfo property = typeof(EditorNestedFlagsEnumTarget).GetProperty(nameof(EditorNestedFlagsEnumTarget.Values))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(property.PropertyType, out var create), Is.True);
        create!(detail, property, [target]);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            object state = RowStateForIndex(detail, 0);
            UIButtonComponent button = (UIButtonComponent)state.GetType().GetProperty("NestedButton")!.GetValue(state)!;
            Assert.That(button.IsEnabled, Is.False);
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void UnchangedNestedDetailPollPreservesRowBindingFocusAndCaret()
    {
        using UITestShaderScope shaders = new();
        SceneNode canvas = new("Nested list canvas", new UICanvasTransform());
        canvas.SetWorldContext(new TestWorldContext());
        canvas.AddComponent<UICanvasComponent>();
        UICanvasInputComponent canvasInput = canvas.AddComponent<UICanvasInputComponent>()!;
        EditorNestedListTarget target = new() { Values = [[123]] };
        SceneNode detail = Create(target);
        detail.Transform.SetParent(canvas.Transform, false, EParentAssignmentMode.Immediate);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode nestedRoot = OpenNestedDetail(detail, outer, 0);
            UIListTransform inner = VirtualList(nestedRoot, 1);
            inner.SynchronizeVirtualRows(280);

            UITextInputComponent input = InputForRow(inner, 0);
            input.CursorPosition = 2;
            input.SelectionAnchor = 0;
            canvasInput.FocusedComponent = input;
            object initialBinding = input.Binding!;
            object initialState = RowStateForIndex(nestedRoot, 0);
            ulong initialRowId = (ulong)initialState.GetType().GetProperty("RowId")!.GetValue(initialState)!;

            Nodes(nestedRoot).SelectMany(node =>
                node.Components.OfType<InspectorListDetailRefreshComponent>()).Single().Refresh!();
            inner.SynchronizeVirtualRows(280);

            UITextInputComponent currentInput = InputForRow(inner, 0);
            object currentState = RowStateForIndex(nestedRoot, 0);
            Assert.Multiple(() =>
            {
                Assert.That(currentState.GetType().GetProperty("RowId")!.GetValue(currentState),
                    Is.EqualTo(initialRowId));
                Assert.That(currentInput, Is.SameAs(input));
                Assert.That(currentInput.Binding, Is.SameAs(initialBinding));
                Assert.That(canvasInput.FocusedComponent, Is.SameAs(input));
                Assert.That(currentInput.CursorPosition, Is.EqualTo(2));
                Assert.That(currentInput.SelectionAnchor, Is.Zero);
            });
        }
        finally { canvas.Destroy(true); }
    }

    [Test]
    public void ReentrantNewNestedPaneSupersedesTheRetiringOpenAttempt()
    {
        using UITestShaderScope shaders = new();
        EditorNestedListTarget target = new() { Values = [[1], [2]] };
        SceneNode detail = Create(target);
        try
        {
            UIListTransform outer = VirtualList(detail, 2);
            outer.SynchronizeVirtualRows(280);
            InvokeNestedAction(detail, 0);
            SceneNode firstPane = CurrentNestedRoot(detail);
            InspectorListDetailRefreshComponent lifetime = Nodes(firstPane)
                .SelectMany(node => node.Components.OfType<InspectorListDetailRefreshComponent>()).Single();
            Action? release = lifetime.Release;
            SceneNode? reentrantPane = null;
            TransformBase? reentrantParent = null;
            lifetime.Release = () =>
            {
                release?.Invoke();
                InvokeNestedAction(detail, 1);
                reentrantPane = Nodes(detail).Single(node => node.Name == "NestedListDetail" &&
                    !ReferenceEquals(node, firstPane) && !node.IsDestroyed && !node.IsDestroyQueued);
                reentrantParent = reentrantPane.Transform.Parent;
            };

            InvokeNestedAction(detail, 1);

            Assert.Multiple(() =>
            {
                Assert.That(reentrantPane, Is.Not.Null);
                Assert.That(CurrentNestedRoot(detail), Is.SameAs(reentrantPane));
                Assert.That(reentrantPane!.IsDestroyed || reentrantPane.IsDestroyQueued, Is.False);
                Assert.That(reentrantPane.Transform.Parent, Is.SameAs(reentrantParent));
            });
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void RebuildPreservesNestedPaneTransferredToAnotherOwner()
    {
        using UITestShaderScope shaders = new();
        EditorNestedListTarget target = new() { Values = [[1]] };
        SceneNode detail = Create(target);
        SceneNode foreign = new("Foreign nested pane host");
        foreign.SetTransform<UIListTransform>();
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode pane = OpenNestedDetail(detail, outer, 0);
            pane.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
            object context = detail.GetComponent<InspectorListDetailRefreshComponent>()!.Release!.Target!;

            context.GetType().GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.Public)!.Invoke(context, null);

            Assert.Multiple(() =>
            {
                Assert.That(pane.IsDestroyed || pane.IsDestroyQueued, Is.False);
                Assert.That(pane.Transform.Parent, Is.SameAs(foreign.Transform));
                Assert.That(pane.IsActiveSelf, Is.True);
            });
        }
        finally
        {
            detail.Destroy(true);
            foreign.Destroy(true);
        }
    }

    [Test]
    public void SourceChangeDuringPaneAttachmentRejectsAndRetiresOnlyThatAttempt()
    {
        using UITestShaderScope shaders = new();
        List<int> original = [1];
        EditorNestedListTarget target = new() { Values = [original] };
        SceneNode detail = Create(target);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            object context = detail.GetComponent<InspectorListDetailRefreshComponent>()!.Release!.Target!;
            SceneNode container = (SceneNode)context.GetType().GetField("_container",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
            List<int> replacement = [9];
            bool changedDuringAttach = false;
            container.Transform.Children.PostAnythingAdded += _ =>
            {
                if (changedDuringAttach) return;
                changedDuringAttach = true;
                target.Values[0] = replacement;
            };

            InvokeNestedAction(detail, 0);

            Assert.Multiple(() =>
            {
                Assert.That(changedDuringAttach, Is.True);
                Assert.That(target.Values[0], Is.SameAs(replacement));
                Assert.That(Nodes(detail).Any(node => node.Name == "NestedListDetail" &&
                    !node.IsDestroyed && !node.IsDestroyQueued), Is.False);
            });
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void TransferDuringFirstCountTextWritePreservesTheNewerLeafIntent()
    {
        using UITestShaderScope shaders = new();
        EditorNestedListTarget target = new() { Values = [[1]] };
        SceneNode detail = Create(target);
        SceneNode foreign = new("Transferred nested pane host");
        foreign.SetTransform<UIListTransform>();
        SceneNode? pane = null;
        UITextComponent? countLabel = null;
        bool wroteNewerText = false;
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            object context = detail.GetComponent<InspectorListDetailRefreshComponent>()!.Release!.Target!;
            SceneNode outerContainer = (SceneNode)context.GetType().GetField("_container",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
            outerContainer.Transform.Children.PostAnythingAdded += transform =>
            {
                if (pane is not null || transform.SceneNode is not { } candidate) return;
                pane = candidate;
                candidate.Transform.Children.PostAnythingAdded += childTransform =>
                {
                    if (childTransform.SceneNode is not { } child) return;
                    child.PropertyChanged += (_, change) =>
                    {
                        if (change.PropertyName != nameof(SceneNode.Transform) ||
                            child.Transform is not UIListTransform) return;
                        child.ComponentAdded += componentPair =>
                        {
                            if (componentPair.comp is not InspectorListDetailRefreshComponent) return;
                            SceneNode innerContainer = child.Transform.Children
                                .Select(item => item.SceneNode).Single(item => item is not null)!;
                            innerContainer.Transform.Children.PostAnythingAdded += labelTransform =>
                            {
                                if (labelTransform.SceneNode is not { } labelNode) return;
                                labelNode.ComponentAdded += labelPair =>
                                {
                                    if (labelPair.comp is not UITextComponent label) return;
                                    countLabel = label;
                                    label.PropertyChanging += (_, textChange) =>
                                    {
                                        if (wroteNewerText || textChange.PropertyName != nameof(UITextComponent.Text) ||
                                            textChange.NewValue is not string incoming || !incoming.StartsWith("Count:", StringComparison.Ordinal))
                                            return;
                                        wroteNewerText = true;
                                        label.SetTextGuarded("callback-owned text", true, static _ => { });
                                        candidate.Transform.SetParent(foreign.Transform, false,
                                            EParentAssignmentMode.Immediate);
                                    };
                                };
                            };
                        };
                    };
                };
            };

            InvokeNestedAction(detail, 0);

            Assert.Multiple(() =>
            {
                Assert.That(wroteNewerText, Is.True);
                Assert.That(countLabel, Is.Not.Null);
                Assert.That(countLabel!.Text, Is.EqualTo("callback-owned text"));
                Assert.That(pane, Is.Not.Null);
                Assert.That(pane!.Transform.Parent, Is.SameAs(foreign.Transform));
                Assert.That(pane.IsDestroyed || pane.IsDestroyQueued, Is.False);
            });
        }
        finally
        {
            detail.Destroy(true);
            foreign.Destroy(true);
        }
    }

    [Test]
    public void ReplacedOuterSlotRejectsNestedWriteAndAcceptedUndo()
    {
        using UITestShaderScope shaders = new();
        List<int> original = [3, 4];
        EditorNestedListTarget target = new() { Values = [original] };
        SceneNode detail = Create(target);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode nestedRoot = OpenNestedDetail(detail, outer, 0);
            UIListTransform inner = VirtualList(nestedRoot, 2);
            inner.SynchronizeVirtualRows(280);
            InputForRow(inner, 0).Binding!.SetValue("30");
            Assert.That(original[0], Is.EqualTo(30));

            List<int> replacement = [100, 200];
            target.Values[0] = replacement;
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(replacement, Is.EqualTo(new[] { 100, 200 }));
            Assert.That(original, Is.EqualTo(new[] { 30, 4 }));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void NestedMultiOwnerSetAndSharedInnerListUseOneUndoStep()
    {
        using UITestShaderScope shaders = new();
        List<int> shared = [5, 6];
        EditorNestedListTarget first = new() { Values = [shared] };
        EditorNestedListTarget second = new() { Values = [shared] };
        SceneNode detail = Create(first, second);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode nestedRoot = OpenNestedDetail(detail, outer, 0);
            UIListTransform inner = VirtualList(nestedRoot, 2);
            inner.SynchronizeVirtualRows(280);
            InputForRow(inner, 1).Binding!.SetValue("60");
            Assert.That(shared, Is.EqualTo(new[] { 5, 60 }));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(shared, Is.EqualTo(new[] { 5, 6 }));
            Assert.That(Undo.CanUndo, Is.False);
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(shared, Is.EqualTo(new[] { 5, 60 }));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void NestedDifferentOwnerListsCommitAtomicallyAndReplayEachValue()
    {
        using UITestShaderScope shaders = new();
        EditorNestedListTarget first = new() { Values = [[1, 2]] };
        EditorNestedListTarget second = new() { Values = [[8, 9]] };
        SceneNode detail = Create(first, second);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode nestedRoot = OpenNestedDetail(detail, outer, 0);
            UIListTransform inner = VirtualList(nestedRoot, 2);
            inner.SynchronizeVirtualRows(280);
            InputForRow(inner, 0).Binding!.SetValue("42");
            Assert.Multiple(() =>
            {
                Assert.That(first.Values[0][0], Is.EqualTo(42));
                Assert.That(second.Values[0][0], Is.EqualTo(42));
            });
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(first.Values[0][0], Is.EqualTo(1));
                Assert.That(second.Values[0][0], Is.EqualTo(8));
            });
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(first.Values[0][0], Is.EqualTo(42));
                Assert.That(second.Values[0][0], Is.EqualTo(42));
            });
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void NestedAppendAndRemoveKeepCanonicalUndoOrder()
    {
        using UITestShaderScope shaders = new();
        EditorNestedListTarget target = new() { Values = [[7]] };
        SceneNode detail = Create(target);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode nestedRoot = OpenNestedDetail(detail, outer, 0);
            UIListTransform inner = VirtualList(nestedRoot, 1);
            inner.SynchronizeVirtualRows(280);

            UIButtonComponent addButton = Nodes(nestedRoot).SelectMany(node => node.Components.OfType<UIButtonComponent>())
                .Single(button => Nodes(button.SceneNode).SelectMany(node => node.Components.OfType<UITextComponent>())
                    .Any(text => text.Text == "Add"));
            InvokeInteract(addButton);
            Assert.That(target.Values[0], Is.EqualTo(new[] { 7, 0 }));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Values[0], Is.EqualTo(new[] { 7 }));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(target.Values[0], Is.EqualTo(new[] { 7, 0 }));

            inner.SynchronizeVirtualRows(280);
            InspectorListDetailRefreshComponent lifetime = Nodes(nestedRoot)
                .SelectMany(node => node.Components.OfType<InspectorListDetailRefreshComponent>()).Single();
            object context = lifetime.Release!.Target!;
            IDictionary cells = (IDictionary)context.GetType().GetField("_listCells",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
            object state = cells.Values.Cast<object>().Single(value =>
                (int)value.GetType().GetProperty("Index")!.GetValue(value)! == 1);
            UIButtonComponent removeButton = (UIButtonComponent)state.GetType().GetProperty("RemoveButton")!.GetValue(state)!;
            InvokeInteract(removeButton);
            Assert.That(target.Values[0], Is.EqualTo(new[] { 7 }));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Values[0], Is.EqualTo(new[] { 7, 0 }));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(target.Values[0], Is.EqualTo(new[] { 7 }));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void FirstNestedAddRebuildsEmptyDetailAndProvidesUndoableInput()
    {
        using UITestShaderScope shaders = new();
        EditorNestedListTarget target = new() { Values = [[]] };
        SceneNode detail = Create(target);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode nestedRoot = OpenNestedDetail(detail, outer, 0);
            Assert.That(Nodes(nestedRoot).Select(node => node.Transform)
                .OfType<UIListTransform>().Any(list => list.VirtualProvider is not null), Is.False);

            UIButtonComponent addButton = Nodes(nestedRoot).SelectMany(node =>
                node.Components.OfType<UIButtonComponent>()).Single(button =>
                Nodes(button.SceneNode).SelectMany(node => node.Components.OfType<UITextComponent>())
                    .Any(text => text.Text == "Add"));
            InvokeInteract(addButton);
            Assert.That(target.Values[0], Is.EqualTo(new[] { 0 }));

            UIListTransform inner = VirtualList(nestedRoot, 1);
            inner.SynchronizeVirtualRows(280);
            UITextInputComponent input = InputForRow(inner, 0);
            input.Binding!.SetValue("17");
            Assert.That(target.Values[0][0], Is.EqualTo(17));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Values[0][0], Is.Zero);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Values[0], Is.Empty);
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void ActiveNestedBindingsAcceptConsecutiveWritesBeforeRowResynchronizes()
    {
        using UITestShaderScope shaders = new();
        EditorNestedListTarget target = new() { Values = [[12]] };
        SceneNode detail = Create(target);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode nestedRoot = OpenNestedDetail(detail, outer, 0);
            UIListTransform inner = VirtualList(nestedRoot, 1);
            inner.SynchronizeVirtualRows(280);
            UITextInputComponent input = InputForRow(inner, 0);
            IUIValueBinding<string> textBinding = input.Binding!;
            List<string> notifications = [];
            textBinding.ValueChanged += notifications.Add;

            textBinding.SetValue("13");
            Assert.That(textBinding.Value, Is.EqualTo("13"));
            textBinding.SetValue("14");
            Assert.That(target.Values[0][0], Is.EqualTo(14));
            Assert.That(textBinding.Value, Is.EqualTo("14"));
            Assert.That(notifications, Is.EqualTo(new[] { "13", "14" }));

            object rowState = RowStateForIndex(nestedRoot, 0);
            IEditorListElementBinding itemBinding = ((IEditorListElementBinding[])
                rowState.GetType().GetProperty("Bindings")!.GetValue(rowState)!)[0];
            itemBinding.GetType().GetProperty("Value")!.SetValue(itemBinding, 15);
            Assert.That(target.Values[0][0], Is.EqualTo(15));
            Assert.That(itemBinding.DisplayedValue, Is.EqualTo(15));
            Assert.That(textBinding.Value, Is.EqualTo("15"));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void MultilineStringNestedRowsRemainUnavailableToFixedHeightEditor()
    {
        using UITestShaderScope shaders = new();
        EditorNestedStringListTarget target = new() { Values = [["line one\nline two"]] };
        SceneNode detail = new("Nested string-list owner detail");
        detail.SetTransform<UIBoundableTransform>();
        PropertyInfo property = typeof(EditorNestedStringListTarget)
            .GetProperty(nameof(EditorNestedStringListTarget.Values))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(
            property.PropertyType, out var create), Is.True);
        create!(detail, property, [target]);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            object context = detail.GetComponent<InspectorListDetailRefreshComponent>()!.Release!.Target!;
            IDictionary cells = (IDictionary)context.GetType().GetField("_listCells",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
            object state = cells.Values.Cast<object>().Single();
            UIButtonComponent button = (UIButtonComponent)state.GetType()
                .GetProperty("NestedButton")!.GetValue(state)!;
            Assert.Multiple(() =>
            {
                Assert.That(button.IsEnabled, Is.False);
                Assert.That(button.SceneNode.IsActiveSelf, Is.False);
                Assert.That(target.Values[0][0], Is.EqualTo("line one\nline two"));
            });
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void SingleLineStringNestedRowsEditWithOneUndoStep()
    {
        using UITestShaderScope shaders = new();
        EditorNestedStringListTarget target = new() { Values = [["first", "second"]] };
        SceneNode detail = new("Nested single-line string detail");
        detail.SetTransform<UIBoundableTransform>();
        PropertyInfo property = typeof(EditorNestedStringListTarget)
            .GetProperty(nameof(EditorNestedStringListTarget.Values))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(
            property.PropertyType, out var create), Is.True);
        create!(detail, property, [target]);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode nestedRoot = OpenNestedDetail(detail, outer, 0);
            UIListTransform inner = VirtualList(nestedRoot, 2);
            inner.SynchronizeVirtualRows(280);

            InputForRow(inner, 1).Binding!.SetValue("edited");

            Assert.That(target.Values[0], Is.EqualTo(new[] { "first", "edited" }));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Values[0], Is.EqualTo(new[] { "first", "second" }));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(target.Values[0][1], Is.EqualTo("edited"));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void VectorNestedRowsEditOneComponentWithOneUndoStep()
    {
        using UITestShaderScope shaders = new();
        EditorNestedVectorListTarget target = new()
        {
            Values = [[new System.Numerics.Vector2(1, 2), new System.Numerics.Vector2(3, 4)]]
        };
        SceneNode detail = new("Nested vector detail");
        detail.SetTransform<UIBoundableTransform>();
        PropertyInfo property = typeof(EditorNestedVectorListTarget)
            .GetProperty(nameof(EditorNestedVectorListTarget.Values))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(
            property.PropertyType, out var create), Is.True);
        create!(detail, property, [target]);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode nestedRoot = OpenNestedDetail(detail, outer, 0);
            UIListTransform inner = VirtualList(nestedRoot, 2);
            inner.SynchronizeVirtualRows(280);
            UITextComponent label = Nodes(inner.SceneNode!).SelectMany(node => node.Components.OfType<UITextComponent>())
                .Single(text => text.Text == "[1]");
            SceneNode row = label.SceneNode.Transform.Parent!.Parent!.SceneNode!;
            UITextInputComponent y = Nodes(row).SelectMany(node => node.Components.OfType<UITextInputComponent>())
                .Single(input => input.SceneNode.Transform.Parent?.SceneNode?.Name == "Y");

            y.Binding!.SetValue("9");

            Assert.That(target.Values[0][1], Is.EqualTo(new System.Numerics.Vector2(3, 9)));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Values[0][1], Is.EqualTo(new System.Numerics.Vector2(3, 4)));
            Assert.That(target.Values[0][0], Is.EqualTo(new System.Numerics.Vector2(1, 2)));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void RecursiveNestedRowsEditTwoLevelsDownWithOneUndoStep()
    {
        using UITestShaderScope shaders = new();
        EditorRecursiveNestedListTarget target = new() { Values = [[[1, 2], [3]]] };
        SceneNode detail = new("Recursive nested detail");
        detail.SetTransform<UIBoundableTransform>();
        PropertyInfo property = typeof(EditorRecursiveNestedListTarget)
            .GetProperty(nameof(EditorRecursiveNestedListTarget.Values))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(
            property.PropertyType, out var create), Is.True);
        create!(detail, property, [target]);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            SceneNode firstRoot = OpenNestedDetail(detail, outer, 0);
            UIListTransform first = VirtualList(firstRoot, 2);
            first.SynchronizeVirtualRows(280);
            SceneNode firstDetail = Nodes(firstRoot)
                .First(node => node.GetComponent<InspectorListDetailRefreshComponent>() is not null);
            InvokeNestedAction(firstDetail, 0);
            SceneNode secondRoot = Nodes(firstRoot).Single(node => node.Name == "NestedListDetail" &&
                !node.IsDestroyed && !node.IsDestroyQueued);
            UIListTransform second = VirtualList(secondRoot, 2);
            second.SynchronizeVirtualRows(280);

            InputForRow(second, 1).Binding!.SetValue("7");

            Assert.That(target.Values[0][0], Is.EqualTo(new[] { 1, 7 }));
            Assert.That(target.Values[0][1], Is.EqualTo(new[] { 3 }));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Values[0][0], Is.EqualTo(new[] { 1, 2 }));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(target.Values[0][0][1], Is.EqualTo(7));
        }
        finally { detail.Destroy(true); }
    }

    [Test]
    public void NestedWorldOwnerWithoutUniqueDocumentIsRejectedBeforeMutation()
    {
        using UITestShaderScope shaders = new();
        SceneNode ownerNode = new("Documentless nested owner");
        ownerNode.SetWorldContext(new TestWorldContext());
        EditorNestedWorldListTarget target = ownerNode.AddComponent<EditorNestedWorldListTarget>()!;
        target.Values = [[11]];
        SceneNode detail = new("Documentless nested detail");
        detail.SetTransform<UIBoundableTransform>();
        PropertyInfo property = typeof(EditorNestedWorldListTarget)
            .GetProperty(nameof(EditorNestedWorldListTarget.Values))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(
            property.PropertyType, out var create), Is.True);
        create!(detail, property, [target]);
        try
        {
            UIListTransform outer = VirtualList(detail, 1);
            outer.SynchronizeVirtualRows(280);
            object state = RowStateForIndex(detail, 0);
            IEditorListElementBinding binding = ((IEditorListElementBinding[])
                state.GetType().GetProperty("Bindings")!.GetValue(state)!)[0];
            UIButtonComponent button = (UIButtonComponent)state.GetType()
                .GetProperty("NestedButton")!.GetValue(state)!;

            Assert.Multiple(() =>
            {
                Assert.That(button.IsEnabled, Is.False);
                Assert.That(() => EditorNestedListSourceLease.Capture(binding),
                    Throws.InvalidOperationException);
                Assert.That(target.Values[0][0], Is.EqualTo(11));
                Assert.That(Undo.CanUndo, Is.False);
            });
        }
        finally
        {
            detail.Destroy(true);
            ownerNode.Destroy(true);
        }
    }

    private static SceneNode Create(params EditorNestedListTarget[] targets)
    {
        SceneNode detail = new("Nested list owner detail");
        detail.SetTransform<UIBoundableTransform>();
        PropertyInfo property = typeof(EditorNestedListTarget).GetProperty(nameof(EditorNestedListTarget.Values))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(property.PropertyType, out var create), Is.True);
        create!(detail, property, targets.Cast<object?>().ToArray());
        return detail;
    }

    private static SceneNode OpenNestedDetail(SceneNode detail, UIListTransform outer, int index)
    {
        InvokeNestedAction(detail, index);
        return CurrentNestedRoot(detail);
    }

    private static void InvokeNestedAction(SceneNode detail, int index)
    {
        object context = detail.GetComponent<InspectorListDetailRefreshComponent>()!.Release!.Target!;
        IDictionary cells = (IDictionary)context.GetType().GetField("_listCells",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
        foreach (DictionaryEntry entry in cells)
        {
            object state = entry.Value!;
            if ((int)state.GetType().GetProperty("Index")!.GetValue(state)! != index) continue;
            Action<UIInteractableComponent> action = (Action<UIInteractableComponent>)
                state.GetType().GetProperty("NestedAction")!.GetValue(state)!;
            UIButtonComponent button = (UIButtonComponent)state.GetType().GetProperty("NestedButton")!.GetValue(state)!;
            Assert.That(action, Is.Not.Null);
            action(button);
            return;
        }
        throw new AssertionException($"No live outer collection row exists for index {index}.");
    }

    private static SceneNode CurrentNestedRoot(SceneNode detail)
        => Nodes(detail).Single(node => node.Name == "NestedListDetail" &&
            !node.IsDestroyed && !node.IsDestroyQueued);

    private static UIListTransform VirtualList(SceneNode root, int count)
        => Nodes(root).Select(node => node.Transform).OfType<UIListTransform>()
            .Single(list => list.VirtualProvider?.Count == count);

    private static object RowStateForIndex(SceneNode root, int index)
    {
        object context = Nodes(root).SelectMany(node =>
            node.Components.OfType<InspectorListDetailRefreshComponent>())
            .Single().Release!.Target!;
        IDictionary cells = (IDictionary)context.GetType().GetField("_listCells",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
        return cells.Values.Cast<object>().Single(state =>
            (int)state.GetType().GetProperty("Index")!.GetValue(state)! == index);
    }

    private static void InvokeInteract(UIInteractableComponent control)
    {
        FieldInfo? eventField = typeof(UIInteractableComponent)
            .GetField("InteractAction", BindingFlags.Instance | BindingFlags.NonPublic);
        Delegate handlers = (Delegate)(eventField?.GetValue(control) ??
            throw new AssertionException("The UI action has no registered callback."));
        handlers.DynamicInvoke(control);
    }

    private static UITextInputComponent InputForRow(UIListTransform list, int index)
    {
        UITextComponent label = Nodes(list.SceneNode!).SelectMany(node => node.Components.OfType<UITextComponent>())
            .Single(text => text.Text == $"[{index}]");
        SceneNode row = label.SceneNode.Transform.Parent!.Parent!.SceneNode!;
        return Nodes(row).SelectMany(node => node.Components.OfType<UITextInputComponent>())
            .Single(input => input.Binding is not null);
    }

    private static IEnumerable<SceneNode> Nodes(SceneNode root)
    {
        yield return root;
        foreach (var child in root.Transform.Children)
            if (child.SceneNode is { } node)
                foreach (SceneNode descendant in Nodes(node)) yield return descendant;
    }

    private sealed class EditorNestedListTarget
    {
        public List<List<int>> Values { get; set; } = [];
    }

    private sealed class EditorNestedStringListTarget
    {
        public List<List<string>> Values { get; set; } = [];
    }

    private sealed class EditorNestedWorldListTarget : XRComponent
    {
        public List<List<int>> Values { get; set; } = [];
    }

    private sealed class EditorNestedVectorListTarget
    {
        public List<List<System.Numerics.Vector2>> Values { get; set; } = [];
    }

    private sealed class EditorRecursiveNestedListTarget
    {
        public List<List<List<int>>> Values { get; set; } = [];
    }

    private sealed class EditorOrdinaryStringListTarget
    {
        public List<string> Values { get; set; } = [];
    }

    private sealed class EditorNestedEnumTarget
    {
        public List<List<EditorNestedEnum>> Values { get; set; } = [];
    }

    private enum EditorNestedEnum { First, Second, Third }

    private sealed class EditorNestedFlagsEnumTarget
    {
        public List<List<EditorNestedFlagsEnum>> Values { get; set; } = [];
    }

    [Flags]
    private enum EditorNestedFlagsEnum { First = 1, Second = 2 }
}
