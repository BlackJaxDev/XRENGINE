using System.Numerics;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Editor;

public sealed partial class NativeHierarchyWorkflowTests
{
    [Test]
    public void ReplacedDisplayLifetimeWhileWaitingForPresentationCancelsRenameFocus()
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, rename) = FocusRename(source);
        try
        {
            rename.Text = "Retained draft";
            Invoke(panel, "OnComponentDeactivated");
            Invoke(panel, "OnComponentActivated");
            UIListTransform list = Realize(panel);
            UITextInputComponent replacement = RenameInput(panel, source);
            Poll(panel);
            Assert.That(input.HasPendingFocusRequest, Is.True);
            UITextComponent display = replacement.TextComponent;
            ulong intent = display.TextWriteIntentRevision;
            display.Generate();
            PresentRename(canvas, list, replacement, 2);
            RefreshPresentedFocus(input);
            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(display.TextWriteIntentRevision, Is.EqualTo(intent));
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [TestCase("text")]
    [TestCase("selection")]
    [TestCase("display")]
    public void ReplacementEditBeforeRestoreKeepsTheNewerIntent(string change)
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, rename) = FocusRename(source);
        try
        {
            rename.Text = "Old draft";
            Invoke(panel, "OnComponentDeactivated");
            Invoke(panel, "OnComponentActivated");
            UIListTransform list = Realize(panel);
            UITextInputComponent replacement = RenameInput(panel, source);
            if (change == "text") replacement.Text = "New draft";
            else if (change == "selection") replacement.CursorPosition = replacement.CursorPosition;
            else replacement.TextComponent.Text = "New display";
            string text = replacement.Text;
            string? display = replacement.TextComponent.Text;
            ulong caretIntent = replacement.CaretWriteIntentRevision;

            Poll(panel);
            PresentRename(canvas, list, replacement, 2);
            RefreshPresentedFocus(input);

            Assert.That(replacement.Text, Is.EqualTo(text));
            Assert.That(replacement.TextComponent.Text, Is.EqualTo(display));
            Assert.That(replacement.CaretWriteIntentRevision, Is.EqualTo(caretIntent));
            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [TestCase(10, 2)]
    [TestCase(2, 10)]
    public void RenameDraftAndSelectionReturnOnlyAfterTheNewRowIsPresented(int caret, int anchor)
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, oldRename) = FocusRename(source);
        try
        {
            oldRename.Text = "Draft title";
            oldRename.CursorPosition = caret;
            oldRename.SelectionAnchor = anchor;
            Assert.That(oldRename.CommitOnSubmit, Is.False);
            ulong oldCaretIntent = oldRename.CaretWriteIntentRevision;
            ulong oldDisplayIntent = oldRename.TextComponent.TextWriteIntentRevision;
            Invoke(panel, "OnComponentDeactivated");
            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(oldRename.CaretWriteIntentRevision, Is.EqualTo(oldCaretIntent + 1));
            Assert.That(oldRename.TextComponent.TextWriteIntentRevision, Is.EqualTo(oldDisplayIntent));
            Assert.That(oldRename.SelectionAnchor, Is.EqualTo(oldRename.CursorPosition));
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Not.Null);

            Invoke(panel, "OnComponentActivated");
            UIListTransform list = Realize(panel);
            UITextInputComponent replacement = RenameInput(panel, source);
            Assert.That(replacement, Is.Not.SameAs(oldRename));
            Poll(panel);
            Assert.That(input.FocusedComponent, Is.Null);
            PresentRename(canvas, list, replacement, 2);
            RefreshPresentedFocus(input);

            Assert.That(input.FocusedComponent, Is.SameAs(replacement));
            Assert.That(replacement.Text, Is.EqualTo("Draft title"));
            Assert.That(replacement.CursorPosition, Is.EqualTo(caret));
            Assert.That(replacement.SelectionAnchor, Is.EqualTo(anchor));
            Assert.That(source.Name, Is.EqualTo("Original"));
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void RenameDraftAndSelectionSurviveTwoStopCycles()
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, first) = FocusRename(source);
        try
        {
            first.Text = "Draft title";
            first.CursorPosition = 8;
            first.SelectionAnchor = 2;
            Invoke(panel, "OnComponentDeactivated");
            UITextInputComponent second = ReactivateAndPresentRename(panel, canvas, input, source, 2);
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(input.FocusedComponent, Is.SameAs(second));
            Assert.That((second.Text, second.CursorPosition, second.SelectionAnchor),
                Is.EqualTo(("Draft title", 8, 2)));

            Invoke(panel, "OnComponentDeactivated");
            UITextInputComponent third = ReactivateAndPresentRename(panel, canvas, input, source, 3);
            Assert.That(third, Is.Not.SameAs(second));
            Assert.That(input.FocusedComponent, Is.SameAs(third));
            Assert.That((third.Text, third.CursorPosition, third.SelectionAnchor),
                Is.EqualTo(("Draft title", 8, 2)));
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [TestCase("equal-null")]
    [TestCase("other-then-null")]
    public void LaterFocusIntentCancelsRenameRestoreAndSavedIdFallback(string change)
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, _) = FocusRename(source);
        try
        {
            Invoke(panel, "OnComponentDeactivated");
            ulong clearedIntent = input.FocusIntentRevision;
            if (change == "other-then-null")
                input.FocusedComponent = new SceneNode(root, "Other focus")
                    .AddComponent<UIButtonComponent>()!;
            input.FocusedComponent = null;
            Assert.That(input.FocusIntentRevision, Is.GreaterThan(clearedIntent));

            ReactivateAndPresentRename(panel, canvas, input, source, 2);
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [TestCase("name")]
    [TestCase("parent")]
    [TestCase("identity")]
    [TestCase("destroy")]
    public void ChangedRenameSourceCannotRecoverItsOldDraftOrFocus(string change)
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, rename) = FocusRename(source);
        SceneNode? replacementParent = null;
        try
        {
            rename.Text = "Draft title";
            Invoke(panel, "OnComponentDeactivated");
            switch (change)
            {
                case "name": source.Name = "Changed externally"; break;
                case "parent":
                    replacementParent = new("Replacement parent");
                    source.Transform.SetParent(replacementParent.Transform, false,
                        EParentAssignmentMode.Immediate);
                    break;
                case "identity": source.Generate(); break;
                default: source.Destroy(true); break;
            }
            Invoke(panel, "OnComponentActivated");
            UIListTransform list = Realize(panel);
            Poll(panel);
            if (!source.IsDestroyed)
            {
                UITextInputComponent replacement = RenameInput(panel, source);
                PresentRename(canvas, list, replacement, 2);
                RefreshPresentedFocus(input);
                Assert.That(replacement.Text, Is.Not.EqualTo("Draft title"));
            }
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally
        {
            root.Destroy(true);
            source.Destroy(true);
            replacementParent?.Destroy(true);
        }
    }

    [Test]
    public void ANewRenameSessionForAnotherSourceWinsBeforeOldFocusPresentation()
    {
        SceneNode source = new("First"), other = new("Second");
        var (root, panel, canvas, input, rename) = FocusRename(source, other);
        try
        {
            rename.Text = "First draft";
            Invoke(panel, "OnComponentDeactivated");
            Invoke(panel, "OnComponentActivated");
            UIListTransform list = Realize(panel);
            Invoke(panel, "BeginRename", other);
            Poll(panel);
            UITextInputComponent oldTarget = RenameInput(panel, source);
            PresentRename(canvas, list, oldTarget, 2);
            RefreshPresentedFocus(input);
            Assert.That(input.FocusedComponent, Is.Not.SameAs(oldTarget));
            UITextInputComponent newTarget = RenameInput(panel, other);
            PresentRename(canvas, list, newTarget, 3);
            RefreshPresentedFocus(input);
            Invoke(panel, "CompleteRenameFocus");
            Assert.That(input.FocusedComponent, Is.SameAs(newTarget));
            Assert.That(Field<SceneNode?>(panel, "_nodePendingRename"), Is.SameAs(other));
        }
        finally { root.Destroy(true); source.Destroy(true); other.Destroy(true); }
    }

    [Test]
    public void ProvisionalCompositionDoesNotResumeOnTheNewRenameInput()
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, rename) = FocusRename(source);
        try
        {
            Assert.That(rename.BeginComposition(), Is.True);
            Assert.That(rename.UpdateComposition("preedit", 7), Is.True);
            Assert.That(rename.IsComposing, Is.True);
            Invoke(panel, "OnComponentDeactivated");
            UITextInputComponent replacement = ReactivateAndPresentRename(
                panel, canvas, input, source, 2);
            Assert.That(replacement.IsComposing, Is.False);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [TestCase("text")]
    [TestCase("selection")]
    [TestCase("reparent")]
    public void RestoreTextCallbackCannotOverwriteANewerInputOwner(string change)
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, rename) = FocusRename(source);
        try
        {
            rename.Text = "Draft title";
            rename.CursorPosition = 8;
            rename.SelectionAnchor = 2;
            Invoke(panel, "OnComponentDeactivated");
            Invoke(panel, "OnComponentActivated");
            Realize(panel);
            UITextInputComponent replacement = RenameInput(panel, source);
            Assert.That(replacement.Text, Is.Not.EqualTo("Draft title"));
            bool callbackRan = false;
            replacement.PropertyChanging += (_, property) =>
            {
                if (callbackRan || property.PropertyName != nameof(UITextInputComponent.Text)) return;
                callbackRan = true;
                switch (change)
                {
                    case "text": replacement.Text = "Callback draft"; break;
                    case "selection":
                        replacement.Text = "Callback selection";
                        replacement.CursorPosition = 2;
                        replacement.SelectionAnchor = 1;
                        break;
                    default:
                        SceneNode parent = new(root, "Callback parent");
                        replacement.SceneNode!.Transform.SetParent(parent.Transform, false,
                            EParentAssignmentMode.Immediate);
                        break;
                }
            };

            Assert.Throws<InvalidOperationException>(() => Poll(panel));
            Assert.That(callbackRan, Is.True);
            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            Assert.That(replacement.Text, change switch
            {
                "text" => Is.EqualTo("Callback draft"),
                "selection" => Is.EqualTo("Callback selection"),
                _ => Is.Not.EqualTo("Draft title"),
            });
            if (change == "selection")
            {
                Assert.That(replacement.CursorPosition, Is.EqualTo(2));
                Assert.That(replacement.SelectionAnchor, Is.EqualTo(1));
            }
            if (change == "reparent")
                Assert.That(replacement.SceneNode!.Transform.Parent?.SceneNode?.Name,
                    Is.EqualTo("Callback parent"));
            else
            {
                PresentRename(canvas, Field<UIListTransform>(panel, "_treeListTransform"),
                    replacement, 2);
                RefreshPresentedFocus(input);
                Assert.That(input.FocusedComponent, Is.Null);
            }
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VisibilityCallbackCannotReplaceTheCapturedRenameDisplay(bool replace)
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, rename) = FocusRename(source);
        UITextComponent? detachedDisplay = null;
        try
        {
            rename.Text = "Draft title";
            Invoke(panel, "OnComponentDeactivated");
            Invoke(panel, "OnComponentActivated");
            Realize(panel);
            object row = Row(panel, source);
            UITextInputComponent replacement = Field<UITextInputComponent>(row, "RenameInput");
            SceneNode inputNode = replacement.SceneNode!;
            UITextComponent display = inputNode.GetComponent<UITextComponent>()!;
            UITextComponent? newerDisplay = null;
            bool callbackRan = false;
            Field<UIBoundableTransform>(row, "LabelTransform").PropertyChanging += (_, property) =>
            {
                if (callbackRan || property.PropertyName != nameof(UIBoundableTransform.Visibility))
                    return;
                callbackRan = true;
                if (replace)
                {
                    Assert.That(inputNode.DetachComponent(display), Is.True);
                    detachedDisplay = display;
                    newerDisplay = inputNode.AddComponent<UITextComponent>()!;
                }
                else
                    display.Text = "Callback display";
            };

            Assert.Throws<InvalidOperationException>(() => Poll(panel));
            Assert.That(callbackRan, Is.True);
            Assert.That(replacement.Text, Is.Not.EqualTo("Draft title"));
            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            if (replace)
                Assert.That(inputNode.GetComponent<UITextComponent>(), Is.SameAs(newerDisplay));
            else
                Assert.That(display.Text, Is.EqualTo("Callback display"));
        }
        finally
        {
            root.Destroy(true);
            detachedDisplay?.Destroy(true);
            source.Destroy(true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FocusClearCallbackCannotCarryAnOldRenameOwnerIntoTheNextRow(bool displayOwner)
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, rename) = FocusRename(source);
        try
        {
            rename.Text = "Draft title";
            UITextComponent display = rename.SceneNode!.GetComponent<UITextComponent>()!;
            bool callbackRan = false;
            input.PropertyChanged += (_, change) =>
            {
                if (callbackRan || change.PropertyName != nameof(UICanvasInputComponent.FocusedComponent) ||
                    input.FocusedComponent is not null) return;
                callbackRan = true;
                if (displayOwner) display.Generate();
                else rename.Generate();
            };

            Invoke(panel, "OnComponentDeactivated");
            Assert.That(callbackRan, Is.True);
            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            UITextInputComponent replacement = ReactivateAndPresentRename(
                panel, canvas, input, source, 2);
            Assert.That(replacement.Text, Is.Not.EqualTo("Draft title"));
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void RejectedPresentedRenameGainPreservesTheGotFocusDisplayEdit()
    {
        SceneNode source = new("Original");
        var (root, panel, canvas, input, original) = FocusRename(source);
        try
        {
            original.Text = "Draft title";
            Invoke(panel, "OnComponentDeactivated");
            Invoke(panel, "OnComponentActivated");
            UIListTransform list = Realize(panel);
            UITextInputComponent replacement = RenameInput(panel, source);
            Poll(panel);
            int gained = 0;
            replacement.GotFocus += _ =>
            {
                gained++;
                replacement.TextComponent.Text = "Callback display";
            };
            PresentRename(canvas, list, replacement, 2);
            RefreshPresentedFocus(input);

            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(replacement.IsFocused, Is.False);
            Assert.That(replacement.TextComponent.Text, Is.EqualTo("Callback display"));
            Assert.That(replacement.Text, Is.EqualTo("Draft title"));
            Assert.That(source.Name, Is.EqualTo("Original"));
            Assert.That(gained, Is.EqualTo(1));
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    private static (SceneNode Root, HierarchyPanel Panel, UICanvasComponent Canvas,
        UICanvasInputComponent Input, UITextInputComponent Rename)
        FocusRename(SceneNode source, params SceneNode[] otherRoots)
    {
        SceneNode[] roots = [source, .. otherRoots];
        var (root, panel) = CreatePanel(roots, actualInput: true);
        root.IsActiveSelf = true;
        UIListTransform list = Realize(panel);
        UICanvasComponent canvas = root.GetComponent<UICanvasComponent>()!;
        UICanvasInputComponent input = root.GetComponent<UICanvasInputComponent>()!;
        Invoke(panel, "BeginRename", source);
        UITextInputComponent rename = RenameInput(panel, source);
        PresentRename(canvas, list, rename, 1);
        RefreshPresentedFocus(input);
        Assert.That(input.TryFocusPresented(rename), Is.True);
        Assert.That(input.FocusedComponent, Is.SameAs(rename));
        return (root, panel, canvas, input, rename);
    }

    private static UITextInputComponent ReactivateAndPresentRename(HierarchyPanel panel,
        UICanvasComponent canvas, UICanvasInputComponent input, SceneNode source, ulong generation)
    {
        Invoke(panel, "OnComponentActivated");
        UIListTransform list = Realize(panel);
        UITextInputComponent rename = RenameInput(panel, source);
        Poll(panel);
        Assert.That(input.FocusedComponent, Is.Null);
        PresentRename(canvas, list, rename, generation);
        RefreshPresentedFocus(input);
        return rename;
    }

    private static UITextInputComponent RenameInput(HierarchyPanel panel, SceneNode source)
        => Field<UITextInputComponent>(Row(panel, source), "RenameInput");

    private static void PresentRename(UICanvasComponent canvas, UIListTransform list,
        UITextInputComponent rename, ulong generation)
    {
        list.SynchronizeVirtualRows(400);
        UIBoundableTransform row = rename.BoundableTransform.Parent as UIBoundableTransform
            ?? throw new AssertionException("The rename input has no row owner.");
        Assert.That(list.TryGetVirtualRowLease(row, out UIVirtualRowLease lease), Is.True);
        Assert.That(list.TryGetRealizedVirtualRow(lease.ItemId, out UIBoundableTransform? current,
            out UIVirtualRowLease currentLease), Is.True);
        Assert.That(current, Is.SameAs(row));
        Assert.That(currentLease, Is.EqualTo(lease));
        BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(400));
        UIHitRecord hit = new(rename.RenderInfo2D, rename.RenderInfo2D.MembershipId,
            rename.UITransform.ElementIdentity, rename.InteractionRevision, Matrix4x4.Identity,
            new Vector2(20), bounds, null, 3, 2, 4, 3, 0, 0, true, false,
            FocusScopeIdentity: canvas.CanvasTransform.ElementIdentity,
            VirtualListIdentity: list.ElementIdentity, VirtualRowIdentity: row.ElementIdentity,
            VirtualRowLease: lease, VirtualProviderIncarnation: list.VirtualProviderIncarnation);
        long revision = canvas.CanvasTransform.ChangeJournal.Revision;
        UIPublication packet = new(generation, canvas.PublicationLifetime, generation, generation,
            revision, default, new UIElementIdentity[] { canvas.CanvasTransform.ElementIdentity,
                list.ElementIdentity, row.ElementIdentity, rename.UITransform.ElementIdentity },
            [hit], [], bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        canvas.MarkPresented(canvas, packet);
        packet.Release();
    }
}
