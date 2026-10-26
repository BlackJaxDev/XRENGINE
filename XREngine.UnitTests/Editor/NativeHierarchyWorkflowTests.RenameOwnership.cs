using NUnit.Framework;
using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Editor;

public sealed partial class NativeHierarchyWorkflowTests
{
    [TestCase("text")]
    [TestCase("selection")]
    [TestCase("reparent")]
    public void FailedRenamePreparationCannotReturnThroughRowRebinding(string change)
    {
        SceneNode source = new("Original"), other = new("Other");
        var (root, panel, _, input, rename) = FocusRename(source, other);
        try
        {
            rename.Text = "Old draft";
            Invoke(panel, "OnComponentDeactivated");
            Invoke(panel, "OnComponentActivated");
            UIListTransform list = Realize(panel);
            UITextInputComponent replacement = RenameInput(panel, source);
            bool callbackRan = false;
            replacement.PropertyChanging += (_, property) =>
            {
                if (callbackRan || property.PropertyName != nameof(UITextInputComponent.Text)) return;
                callbackRan = true;
                if (change == "reparent")
                {
                    SceneNode parent = new(root, "Callback parent");
                    replacement.SceneNode.Transform.SetParent(parent.Transform, false,
                        EParentAssignmentMode.Immediate);
                    replacement.Text = "Transferred draft";
                    replacement.BoundableTransform.Visibility = EVisibility.Visible;
                }
                else
                {
                    replacement.Text = "Callback draft";
                    if (change == "selection")
                    {
                        replacement.CursorPosition = 2;
                        replacement.SelectionAnchor = 1;
                    }
                }
            };

            Assert.Throws<InvalidOperationException>(() => Poll(panel));
            Assert.That(callbackRan, Is.True);
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            Assert.That(Field<SceneNode?>(panel, "_nodePendingRename"), Is.Null);
            Assert.That(Field<bool>(panel, "_renameFocusRequested"), Is.False);
            if (change == "selection")
                Assert.That((replacement.Text, replacement.CursorPosition, replacement.SelectionAnchor),
                    Is.EqualTo(("Callback draft", 2, 1)));

            // Rebind in this active panel. Another stop or rename request would hide the stale session.
            list.VirtualOverscan = 0;
            list.SynchronizeVirtualRows(30);
            Assert.That(list.TryScrollToVirtualItem(Id(panel, other)), Is.True);
            list.SynchronizeVirtualRows(30);
            Assert.That(list.TryGetRealizedVirtualRow(Id(panel, source), out _, out _), Is.False);
            Assert.That(list.TryScrollToVirtualItem(Id(panel, source)), Is.True);
            list.SynchronizeVirtualRows(30);
            object rebound = Row(panel, source);
            UIBoundableTransform reboundInputTransform = Field<UIBoundableTransform>(rebound, "RenameTransform");
            if (ReferenceEquals(reboundInputTransform.Parent, Field<UIBoundableTransform>(rebound, "Transform")))
                Assert.That(reboundInputTransform.Visibility, Is.EqualTo(EVisibility.Hidden));
            Assert.That(Field<UITextInputComponent>(rebound, "RenameInput").Text, Is.Not.EqualTo("Old draft"));
            Assert.That(Field<SceneNode?>(panel, "_nodePendingRename"), Is.Null);
            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(input.HasPendingFocusRequest, Is.False);
            if (change == "reparent")
            {
                Assert.That(replacement.SceneNode.Transform.Parent?.SceneNode?.Name,
                    Is.EqualTo("Callback parent"));
                Assert.That(replacement.Text, Is.EqualTo("Transferred draft"));
                Assert.That(replacement.BoundableTransform.Visibility, Is.EqualTo(EVisibility.Visible));
            }
        }
        finally { root.Destroy(true); source.Destroy(true); other.Destroy(true); }
    }

    [TestCase("label")]
    [TestCase("input")]
    public void RenameVisibilityCallbackCannotWriteToATransferredInput(string boundary)
    {
        SceneNode source = new("Original");
        var (root, panel, _, input, rename) = FocusRename(source);
        try
        {
            rename.Text = "Old draft";
            Invoke(panel, "OnComponentDeactivated");
            Invoke(panel, "OnComponentActivated");
            Realize(panel);
            object row = Row(panel, source);
            UITextInputComponent replacement = Field<UITextInputComponent>(row, "RenameInput");
            UIBoundableTransform renameTransform = Field<UIBoundableTransform>(row, "RenameTransform");
            UIBoundableTransform callbackTransform = boundary == "label"
                ? Field<UIBoundableTransform>(row, "LabelTransform") : renameTransform;
            bool callbackRan = false;
            callbackTransform.PropertyChanging += (_, property) =>
            {
                if (callbackRan || property.PropertyName != nameof(UIBoundableTransform.Visibility)) return;
                callbackRan = true;
                SceneNode parent = new(root, "Transferred input");
                renameTransform.SetParent(parent.Transform, false, EParentAssignmentMode.Immediate);
                replacement.Text = "Transferred draft";
                renameTransform.Visibility = EVisibility.Collapsed;
            };

            Assert.Throws<InvalidOperationException>(() => Poll(panel));
            Assert.That(callbackRan, Is.True);
            Assert.That(renameTransform.Parent?.SceneNode?.Name, Is.EqualTo("Transferred input"));
            Assert.That(renameTransform.Visibility, Is.EqualTo(EVisibility.Collapsed));
            Assert.That(replacement.Text, Is.EqualTo("Transferred draft"));
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            Assert.That(Field<SceneNode?>(panel, "_nodePendingRename"), Is.Null);
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LabelVisibilityCallbackCannotReplaceANewerRenameVisibilityIntent(bool equalValue)
    {
        SceneNode source = new("Original");
        var (root, panel, _, input, rename) = FocusRename(source);
        try
        {
            rename.Text = "Old draft";
            Invoke(panel, "OnComponentDeactivated");
            Invoke(panel, "OnComponentActivated");
            Realize(panel);
            object row = Row(panel, source);
            UIBoundableTransform label = Field<UIBoundableTransform>(row, "LabelTransform");
            UIBoundableTransform renameTransform = Field<UIBoundableTransform>(row, "RenameTransform");
            EVisibility requestedVisibility = equalValue ? renameTransform.Visibility : EVisibility.Collapsed;
            ulong initialIntent = renameTransform.VisibilityWriteIntentRevision;
            bool callbackRan = false;
            label.PropertyChanging += (_, property) =>
            {
                if (callbackRan || property.PropertyName != nameof(UIBoundableTransform.Visibility)) return;
                callbackRan = true;
                renameTransform.Visibility = requestedVisibility;
            };

            Assert.Throws<InvalidOperationException>(() => Poll(panel));
            Assert.That(callbackRan, Is.True);
            Assert.That(renameTransform.Visibility, Is.EqualTo(requestedVisibility));
            Assert.That(renameTransform.VisibilityWriteIntentRevision, Is.EqualTo(initialIntent + 1));
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            Assert.That(Field<SceneNode?>(panel, "_nodePendingRename"), Is.Null);
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedRenamePreparationPreservesACallbackRenameRequest(bool sameSource)
    {
        SceneNode source = new("Original"), other = new("Other");
        var (root, panel, _, _, rename) = FocusRename(source, other);
        try
        {
            rename.Text = "Old draft";
            Invoke(panel, "OnComponentDeactivated");
            Invoke(panel, "OnComponentActivated");
            Realize(panel);
            UITextInputComponent replacement = RenameInput(panel, source);
            SceneNode requested = sameSource ? source : other;
            bool callbackRan = false;
            ulong requestedSession = 0;
            replacement.PropertyChanging += (_, property) =>
            {
                if (callbackRan || property.PropertyName != nameof(UITextInputComponent.Text)) return;
                callbackRan = true;
                Invoke(panel, "BeginRename", requested);
                requestedSession = Field<ulong>(panel, "_renameSession");
            };

            Assert.Throws<InvalidOperationException>(() => Poll(panel));
            Assert.That(callbackRan, Is.True);
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            Assert.That(Field<SceneNode?>(panel, "_nodePendingRename"), Is.SameAs(requested));
            Assert.That(Field<ulong>(panel, "_renameSession"), Is.EqualTo(requestedSession));
            Assert.That(Field<bool>(panel, "_renameFocusRequested"), Is.True);
        }
        finally { root.Destroy(true); source.Destroy(true); other.Destroy(true); }
    }

    [TestCase("provider")]
    [TestCase("reparent")]
    [TestCase("display")]
    public void FocusClearRejectsChangedRenameRowOwnership(string change)
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, rename) = FocusRename(source);
        try
        {
            rename.Text = "Old draft";
            UIListTransform list = Field<UIListTransform>(panel, "_treeListTransform");
            bool callbackRan = false;
            input.PropertyChanged += (_, property) =>
            {
                if (callbackRan || property.PropertyName != nameof(UICanvasInputComponent.FocusedComponent) ||
                    input.FocusedComponent is not null) return;
                callbackRan = true;
                if (change == "provider")
                    list.DetachVirtualProviderForOwnerTeardown();
                else if (change == "display")
                    rename.TextComponent.Text = "Callback display";
                else
                {
                    SceneNode parent = new(root, "Transferred input");
                    rename.SceneNode.Transform.SetParent(parent.Transform, false,
                        EParentAssignmentMode.Immediate);
                }
            };

            Invoke(panel, "OnComponentDeactivated");
            Assert.That(callbackRan, Is.True);
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            UITextInputComponent replacement = ReactivateAndPresentRename(panel, canvas, input, source, 2);
            Assert.That(replacement.Text, Is.Not.EqualTo("Old draft"));
            Assert.That(input.FocusedComponent, Is.Null);
            if (change == "display")
                Assert.That(rename.TextComponent.Text, Is.EqualTo("Callback display"));
            if (change == "reparent")
            {
                Assert.That(rename.Text, Is.EqualTo("Old draft"));
                Assert.That(rename.SceneNode.Transform.Parent?.SceneNode?.Name,
                    Is.EqualTo("Transferred input"));
            }
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [TestCase("text")]
    [TestCase("equal-caret")]
    [TestCase("display")]
    [TestCase("reparent")]
    public void RecyclingCallbackCannotCarryTheCapturedRenameDraftIntoTheNextRow(string change)
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, rename) = FocusRename(source);
        try
        {
            Assert.That(rename.CommitOnSubmit, Is.False);
            rename.Text = "Old draft";
            rename.CursorPosition = 7;
            rename.SelectionAnchor = 2;
            ulong caretIntent = rename.CaretWriteIntentRevision;
            UIListTransform list = Field<UIListTransform>(panel, "_treeListTransform");
            UIBoundableTransform row = Field<UIBoundableTransform>(Row(panel, source), "Transform");
            bool callbackRan = false;
            list.VirtualRowRecycling += (retiredRow, _) =>
            {
                if (callbackRan || !ReferenceEquals(retiredRow, row)) return;
                callbackRan = true;
                switch (change)
                {
                    case "text": rename.Text = "Callback draft"; break;
                    case "equal-caret": rename.CursorPosition = rename.CursorPosition; break;
                    case "display": rename.TextComponent.Text = "Callback display"; break;
                    default:
                        SceneNode parent = new(root, "Transferred input");
                        rename.SceneNode.Transform.SetParent(parent.Transform, false,
                            EParentAssignmentMode.Immediate);
                        break;
                }
            };

            Invoke(panel, "OnComponentDeactivated");
            Assert.That(callbackRan, Is.True);
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            if (change == "equal-caret")
                Assert.That(rename.CaretWriteIntentRevision, Is.EqualTo(caretIntent + 2));
            if (change == "text")
                Assert.That(rename.Text, Is.EqualTo("Callback draft"));
            if (change == "display")
                Assert.That(rename.TextComponent.Text, Is.EqualTo("Callback display"));
            if (change == "reparent")
            {
                Assert.That(rename.CaretWriteIntentRevision, Is.EqualTo(caretIntent));
                Assert.That((rename.CursorPosition, rename.SelectionAnchor), Is.EqualTo((7, 2)));
                Assert.That(rename.SceneNode.Transform.Parent?.SceneNode?.Name,
                    Is.EqualTo("Transferred input"));
            }
            UITextInputComponent replacement = ReactivateAndPresentRename(panel, canvas, input, source, 2);
            Assert.That(replacement.Text, Is.Not.EqualTo("Old draft"));
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void LateRowCleanupDisplayEditCancelsRenameRestore()
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, rename) = FocusRename(source);
        try
        {
            rename.Text = "Old draft";
            var button = Field<UIButtonComponent>(Row(panel, source), "Button");
            bool callbackRan = false;
            button.PropertyChanged += (_, property) =>
            {
                if (callbackRan || property.PropertyName != nameof(UIButtonComponent.UserData) ||
                    button.UserData is not null) return;
                callbackRan = true;
                rename.TextComponent.Text = "Late callback display";
            };

            Invoke(panel, "OnComponentDeactivated");
            Assert.That(callbackRan, Is.True);
            Assert.That(rename.TextComponent.Text, Is.EqualTo("Late callback display"));
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            UITextInputComponent replacement = ReactivateAndPresentRename(panel, canvas, input, source, 2);
            Assert.That(replacement.Text, Is.Not.EqualTo("Old draft"));
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }
}
