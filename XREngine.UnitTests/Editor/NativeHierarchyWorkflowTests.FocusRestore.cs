using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Editor;

public sealed partial class NativeHierarchyWorkflowTests
{
    [Test]
    public void OrdinaryRowFocusReturnsOnlyAfterTheReplacementRowIsPresented()
    {
        SceneNode source = new("Focused source"), other = new("Selected elsewhere");
        Selection.SceneNodes = [other];
        var (root, panel, canvas, input, oldButton) = FocusOrdinaryRow(source);
        try
        {
            SceneNode[] selected = Selection.SceneNodes;
            StopFocusOwner(panel, input);
            Invoke(panel, "OnComponentActivated");
            UIListTransform list = Realize(panel);
            UIVirtualTreeRowButtonComponent replacement =
                Field<UIVirtualTreeRowButtonComponent>(Row(panel, source), "Button");
            Assert.That(replacement, Is.Not.SameAs(oldButton));
            Poll(panel);
            Assert.That(input.FocusedComponent, Is.Null);
            PresentRow(canvas, list, replacement, 2);
            RefreshPresentedFocus(input);

            Assert.That(input.FocusedComponent, Is.SameAs(replacement));
            Assert.That(Selection.SceneNodes, Is.SameAs(selected));
        }
        finally { root.Destroy(true); source.Destroy(true); other.Destroy(true); }
    }

    [TestCase("equal-null")]
    [TestCase("other-then-null")]
    [TestCase("old-then-null")]
    public void LaterFocusIntentCancelsOrdinaryRowRestore(string change)
    {
        SceneNode source = new("Focused source");
        var (root, panel, canvas, input, oldButton) = FocusOrdinaryRow(source);
        try
        {
            StopFocusOwner(panel, input);
            ulong clearedIntent = input.FocusIntentRevision;
            switch (change)
            {
                case "equal-null":
                    input.FocusedComponent = null;
                    break;
                case "other-then-null":
                    input.FocusedComponent = new SceneNode(root, "Other focus")
                        .AddComponent<UIButtonComponent>()!;
                    input.FocusedComponent = null;
                    break;
                default:
                    input.FocusedComponent = oldButton;
                    input.FocusedComponent = null;
                    break;
            }
            Assert.That(input.FocusIntentRevision, Is.GreaterThan(clearedIntent));
            ReactivateAndPresent(panel, canvas, input, source);
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void PreclearFocusCallbackPreservesItsNewTarget()
    {
        SceneNode source = new("Focused source");
        var (root, panel, canvas, input, oldButton) = FocusOrdinaryRow(source);
        SceneNode outsideNode = new(root, "Callback focus");
        UIButtonComponent outside = outsideNode.AddComponent<UIButtonComponent>()!;
        try
        {
            bool changed = false;
            input.PropertyChanging += (_, change) =>
            {
                if (changed || change.PropertyName != nameof(UICanvasInputComponent.FocusedComponent) ||
                    !ReferenceEquals(input.FocusedComponent, oldButton)) return;
                changed = true;
                input.FocusedComponent = outside;
            };
            Invoke(panel, "OnComponentDeactivated");
            Assert.That(changed, Is.True);
            Assert.That(input.FocusedComponent, Is.SameAs(outside));
            ReactivateAndPresent(panel, canvas, input, source);
            Assert.That(input.FocusedComponent, Is.SameAs(outside));
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RemovedOrDestroyedSourceCannotRecoverOldRowFocus(bool destroy)
    {
        SceneNode source = new("Focused source");
        var (root, panel, canvas, input, _) = FocusOrdinaryRow(source);
        try
        {
            StopFocusOwner(panel, input);
            if (destroy) source.Destroy(true);
            else panel.RootNodes = [];
            Invoke(panel, "OnComponentActivated");
            UIListTransform list = Realize(panel);
            Poll(panel);
            list.SynchronizeVirtualRows(400);
            RefreshPresentedFocus(input);
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void ChangedPanelParentRejectsItsCapturedFocusOwner()
    {
        SceneNode source = new("Focused source");
        var (root, panel, canvas, input, _) = FocusOrdinaryRow(source);
        try
        {
            StopFocusOwner(panel, input);
            SceneNode parent = new(root, "Replacement parent");
            panel.SceneNode!.Transform.SetParent(parent.Transform, false,
                EParentAssignmentMode.Immediate);
            ReactivateAndPresent(panel, canvas, input, source);
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void ChangedCanvasIdentityRejectsItsCapturedFocusOwner()
    {
        SceneNode source = new("Focused source");
        var (root, panel, canvas, input, _) = FocusOrdinaryRow(source);
        try
        {
            StopFocusOwner(panel, input);
            canvas.Generate();
            ReactivateAndPresent(panel, canvas, input, source);
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void ChangedSourceIdentityRejectsItsCapturedFocusOwner()
    {
        SceneNode source = new("Focused source");
        var (root, panel, canvas, input, _) = FocusOrdinaryRow(source);
        try
        {
            StopFocusOwner(panel, input);
            source.Generate();
            ReactivateAndPresent(panel, canvas, input, source);
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void OrdinaryRowFocusSurvivesTwoCompleteStopAndPresentationCycles()
    {
        SceneNode source = new("Focused source");
        var (root, panel, canvas, input, first) = FocusOrdinaryRow(source);
        try
        {
            StopFocusOwner(panel, input);
            ReactivateAndPresent(panel, canvas, input, source);
            UIVirtualTreeRowButtonComponent second =
                Field<UIVirtualTreeRowButtonComponent>(Row(panel, source), "Button");
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(input.FocusedComponent, Is.SameAs(second));

            StopFocusOwner(panel, input);
            Invoke(panel, "OnComponentActivated");
            UIListTransform list = Realize(panel);
            UIVirtualTreeRowButtonComponent third =
                Field<UIVirtualTreeRowButtonComponent>(Row(panel, source), "Button");
            Assert.That(third, Is.Not.SameAs(second));
            Poll(panel);
            Assert.That(input.FocusedComponent, Is.Null);
            PresentRow(canvas, list, third, 3);
            RefreshPresentedFocus(input);
            Assert.That(input.FocusedComponent, Is.SameAs(third));
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void RecycleFailureCancelsTheFocusReceipt()
    {
        SceneNode source = new("Focused source");
        var (root, panel, canvas, input, _) = FocusOrdinaryRow(source);
        try
        {
            UIListTransform list = Field<UIListTransform>(panel, "_treeListTransform");
            InvalidOperationException expected = new("Recycle failed");
            int callbacks = 0;
            list.VirtualRowRecycling += (_, _) =>
            {
                callbacks++;
                throw expected;
            };
            AggregateException error = Assert.Throws<AggregateException>(
                () => Invoke(panel, "OnComponentDeactivated"))!;
            Assert.That(error.InnerExceptions, Does.Contain(expected));
            Assert.That(callbacks, Is.GreaterThan(0));
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            ReactivateAndPresent(panel, canvas, input, source);
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void VetoedFocusClearCannotBecomeARestorationRequest()
    {
        SceneNode source = new("Focused source");
        var (root, panel, canvas, input, oldButton) = FocusOrdinaryRow(source);
        try
        {
            bool vetoed = false;
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName != nameof(UICanvasInputComponent.FocusedComponent) ||
                    !ReferenceEquals(input.FocusedComponent, oldButton)) return;
                vetoed = true;
                ((XRPropertyChangingEventArgs<UIInteractableComponent?>)change).AllowChange = false;
            };
            Invoke(panel, "OnComponentDeactivated");
            Assert.That(vetoed, Is.True);
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            ReactivateAndPresent(panel, canvas, input, source);
            UIVirtualTreeRowButtonComponent replacement =
                Field<UIVirtualTreeRowButtonComponent>(Row(panel, source), "Button");
            Assert.That(input.FocusedComponent, Is.Not.SameAs(replacement));
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    [TestCase("rename")]
    [TestCase("arrow")]
    [TestCase("toggle")]
    public void AuxiliaryRowFocusDoesNotBecomeAnOrdinaryRowFocusReceipt(string control)
    {
        SceneNode source = new("Focused source");
        var (root, panel, canvas, input, _) = FocusOrdinaryRow(source);
        try
        {
            object row = Row(panel, source);
            UIInteractableComponent auxiliary = control switch
            {
                "rename" => Field<UITextInputComponent>(row, "RenameInput"),
                "arrow" => Field<UIButtonComponent>(row, "Arrow"),
                _ => Field<UIButtonComponent>(row, "Toggle"),
            };
            input.FocusedComponent = auxiliary;
            Invoke(panel, "OnComponentDeactivated");
            Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Null);
            ReactivateAndPresent(panel, canvas, input, source);
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); source.Destroy(true); }
    }

    private static (SceneNode Root, HierarchyPanel Panel, UICanvasComponent Canvas,
        UICanvasInputComponent Input, UIVirtualTreeRowButtonComponent Button)
        FocusOrdinaryRow(SceneNode source)
    {
        var (root, panel) = CreatePanel([source], actualInput: true);
        root.IsActiveSelf = true;
        UIListTransform list = Realize(panel);
        UICanvasComponent canvas = root.GetComponent<UICanvasComponent>()!;
        UICanvasInputComponent input = root.GetComponent<UICanvasInputComponent>()!;
        UIVirtualTreeRowButtonComponent button =
            Field<UIVirtualTreeRowButtonComponent>(Row(panel, source), "Button");
        PresentRow(canvas, list, button, 1);
        Assert.That(input.TryFocusPresented(button), Is.True);
        Assert.That(input.FocusedComponent, Is.SameAs(button));
        return (root, panel, canvas, input, button);
    }

    private static void StopFocusOwner(HierarchyPanel panel, UICanvasInputComponent input)
    {
        Invoke(panel, "OnComponentDeactivated");
        Assert.That(input.FocusedComponent, Is.Null);
        Assert.That(Field<object?>(panel, "_rowFocusReceipt"), Is.Not.Null);
    }

    private static void ReactivateAndPresent(HierarchyPanel panel, UICanvasComponent canvas,
        UICanvasInputComponent input, SceneNode source)
    {
        Invoke(panel, "OnComponentActivated");
        UIListTransform list = Realize(panel);
        UIVirtualTreeRowButtonComponent button =
            Field<UIVirtualTreeRowButtonComponent>(Row(panel, source), "Button");
        Poll(panel);
        PresentRow(canvas, list, button, 2);
        RefreshPresentedFocus(input);
    }

    private static void RefreshPresentedFocus(UICanvasInputComponent input)
    {
        try
        {
            UIPublication? packet = (UIPublication?)typeof(UICanvasInputComponent)
                .GetMethod("RefreshPresentedInput", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(input, null);
            packet?.Release();
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }
}
