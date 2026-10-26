using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

public sealed partial class UITextGuardedCanvasFocusTests
{
    [Test]
    public void GuardedPresentedFocusKeepsDraftAndSelection()
    {
        _control.CursorPosition = 3;
        _control.SelectionAnchor = 1;
        PresentSearchTarget();
        Assert.That(_input.TryFocusPresented(_control, _input.FocusIntentRevision, static () => true), Is.True);
        Assert.That(_input.FocusedComponent, Is.SameAs(_control));
        Assert.That((_control.Text, _control.CursorPosition, _control.SelectionAnchor), Is.EqualTo(("seed", 3, 1)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GuardedPresentedFocusRejectsMissingPresentationOrCaller(bool present)
    {
        if (present) PresentSearchTarget();
        ulong intent = _input.FocusIntentRevision;
        Assert.That(_input.TryFocusPresented(_control, intent, () => !present), Is.False);
        Assert.That(_input.FocusIntentRevision, Is.EqualTo(intent));
        Assert.That(_input.FocusedComponent, Is.Null);
    }

    [Test]
    public void GuardedPresentedFocusDoesNotReplaceAnotherFocusedControl()
    {
        SceneNode otherNode = new(_root, "Other focus", new UIBoundableTransform());
        var other = otherNode.AddComponent<UIButtonComponent>()!;
        _input.FocusedComponent = other;
        PresentSearchTarget();
        ulong intent = _input.FocusIntentRevision;
        Assert.That(_input.TryFocusPresented(_control, intent, static () => true), Is.False);
        Assert.That(_input.FocusedComponent, Is.SameAs(other));
        Assert.That(_input.FocusIntentRevision, Is.EqualTo(intent));
    }

    [Test]
    public void GuardedPresentedFocusPreservesNewerCanvasIntent()
    {
        SceneNode otherNode = new(_root, "Newer focus", new UIBoundableTransform());
        var other = otherNode.AddComponent<UIButtonComponent>()!;
        PresentSearchTarget();
        bool changed = false;
        _input.PropertyChanging += (_, change) =>
        {
            if (changed || change.PropertyName != nameof(UICanvasInputComponent.FocusedComponent)) return;
            changed = true;
            _input.FocusedComponent = other;
        };
        Assert.That(_input.TryFocusPresented(_control, _input.FocusIntentRevision, static () => true), Is.False);
        Assert.That(changed, Is.True);
        Assert.That(_input.FocusedComponent, Is.SameAs(other));
        Assert.That(_control.IsFocused, Is.False);
    }

    [Test]
    public void GuardedPresentedFocusRejectsCallerLossBeforeControlGain()
    {
        PresentSearchTarget();
        bool current = true;
        _control.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(UITextInputComponent.IsFocused)) current = false;
        };
        Assert.That(_input.TryFocusPresented(_control, _input.FocusIntentRevision, () => current), Is.False);
        Assert.That(_control.IsFocused, Is.False);
        Assert.That(_input.FocusedComponent, Is.Null);
    }

    [Test]
    public void GuardedPresentedFocusRejectsTransferredTargetBeforeAssignment()
    {
        SceneNode foreign = new(_root, "Foreign parent", new UIBoundableTransform());
        PresentSearchTarget();
        bool moved = false;
        _input.PropertyChanging += (_, change) =>
        {
            if (moved || change.PropertyName != nameof(UICanvasInputComponent.FocusedComponent)) return;
            moved = true;
            _control.Transform.SetParent(foreign.Transform, false, XREngine.Scene.Transforms.EParentAssignmentMode.Immediate);
        };
        Assert.That(_input.TryFocusPresented(_control, _input.FocusIntentRevision, static () => true), Is.False);
        Assert.That(_control.IsFocused, Is.False);
        Assert.That(_control.Transform.Parent, Is.SameAs(foreign.Transform));
    }

    [Test]
    public void GuardedPresentedFocusPreservesNewerGotFocusDraft()
    {
        PresentSearchTarget();
        _control.GotFocus += _ => { _control.Text = "newer draft"; _control.CursorPosition = 2; };
        Assert.That(_input.TryFocusPresented(_control, _input.FocusIntentRevision, static () => true), Is.False);
        Assert.That(_control.Text, Is.EqualTo("newer draft"));
        Assert.That(_control.CursorPosition, Is.EqualTo(2));
        Assert.That(_control.IsFocused, Is.False);
    }

    [Test]
    public void GuardedPresentedFocusRejectsStaleVirtualTarget()
    {
        PresentSearchTarget(virtualTarget: true);
        ((UIVirtualTreeProvider)_list.VirtualProvider!).SetSelected(1, true);
        ulong intent = _input.FocusIntentRevision;
        Assert.That(_input.TryFocusPresented(_control, intent, static () => true), Is.False);
        Assert.That(_input.FocusedComponent, Is.Null);
        Assert.That(_input.FocusIntentRevision, Is.EqualTo(intent));
    }

    private void PresentSearchTarget(bool virtualTarget = false)
    {
        if (!virtualTarget)
        {
            var previous = _control;
            SceneNode node = new(_root, "Search toolbar", new UIBoundableTransform());
            node.AddComponent<UITextComponent>();
            _control = node.AddComponent<UITextInputComponent>()!;
            _control.PreText = previous.PreText;
            _control.PostText = previous.PostText;
            _control.Text = previous.Text;
            _control.CursorPosition = previous.CursorPosition;
            _control.SelectionAnchor = previous.SelectionAnchor;
        }
        BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(100));
        UIHitRecord hit = new(_control.RenderInfo2D, _control.RenderInfo2D.MembershipId,
            _control.UITransform.ElementIdentity, _control.InteractionRevision,
            Matrix4x4.Identity, new Vector2(20), bounds, null, 3, 2, 4, 3, 0, 0, true, false,
            FocusScopeIdentity: _canvas.CanvasTransform.ElementIdentity,
            VirtualListIdentity: virtualTarget ? _list.ElementIdentity : default,
            VirtualRowIdentity: virtualTarget ? _row.ElementIdentity : default,
            VirtualRowLease: virtualTarget ? _lease : default,
            VirtualProviderIncarnation: virtualTarget ? _list.VirtualProviderIncarnation : 0);
        UIPublication packet = new(1, _canvas.PublicationLifetime, 1, 1,
            _canvas.CanvasTransform.ChangeJournal.Revision, default,
            new UIElementIdentity[] { _canvas.CanvasTransform.ElementIdentity,
                _list.ElementIdentity, _row.ElementIdentity, _control.UITransform.ElementIdentity },
            [hit], [], bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        _canvas.MarkPresented(_canvas, packet);
        packet.Release();
        UIPublication? read = (UIPublication?)typeof(UICanvasInputComponent)
            .GetMethod("RefreshPresentedInput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_input, null);
        read?.Release();
    }
}
