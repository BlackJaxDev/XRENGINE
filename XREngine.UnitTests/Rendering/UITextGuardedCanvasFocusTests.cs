using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITextGuardedCanvasFocusTests
{
    private UITestShaderScope _shaders = null!;
    private SceneNode _root = null!;
    private UICanvasComponent _canvas = null!;
    private UICanvasInputComponent _input = null!;
    private LocalInputInterface _device = null!;
    private UIListTransform _list = null!;
    private UIBoundableTransform _row = null!;
    private UIVirtualRowLease _lease;
    private UITextInputComponent _control = null!;

    [SetUp]
    public void SetUp()
    {
        _shaders = new UITestShaderScope();
        _root = new SceneNode("Guarded text focus", new UICanvasTransform());
        _root.SetWorldContext(new TestWorldContext());
        _canvas = _root.AddComponent<UICanvasComponent>()!;
        _input = _root.AddComponent<UICanvasInputComponent>()!;
        _device = new LocalInputInterface();
        _input.RegisterInput(_device);
        _list = new UIListTransform { ItemSize = 20, VirtualOverscan = 0 };
        SceneNode listNode = new(_root, "List", _list);
        UIVirtualTreeProvider provider = new(() =>
        {
            UIBoundableTransform row = new();
            SceneNode node = new(listNode, "Row", row);
            node.AddComponent<UITextComponent>();
            node.AddComponent<UITextInputComponent>();
            return row;
        }, (_, _, _, _) => { }, (_, _, _) => { });
        provider.Insert(1, 0, 0, "First");
        _list.VirtualProvider = provider;
        _list.SynchronizeVirtualRows(20);
        Assert.That(_list.TryGetRealizedVirtualRow(1, out UIBoundableTransform? row, out _lease), Is.True);
        _row = row!;
        _control = _row.SceneNode!.GetComponent<UITextInputComponent>()!;
        _control.PreText = "[";
        _control.PostText = "]";
        _control.Text = "seed";
    }

    [TearDown]
    public void TearDown()
    {
        _root?.Destroy(true);
        _shaders?.Dispose();
    }

    [Test]
    public void RejectedGainPreservesTheGotFocusDisplayEdit()
    {
        int gained = 0, lost = 0;
        _control.GotFocus += _ => { gained++; _control.TextComponent.Text = "callback display"; };
        _control.LostFocus += _ => lost++;
        RequestAndPresent();
        AssertCleared();
        Assert.That(_control.TextComponent.Text, Is.EqualTo("callback display"));
        Assert.That(_control.Text, Is.EqualTo("seed"));
        Assert.That(gained, Is.EqualTo(1));
        Assert.That(lost, Is.EqualTo(1));
    }

    [Test]
    public void RejectedGainPreservesNewerDraftCaretAndDisplay()
    {
        _control.GotFocus += _ =>
        {
            _control.Text = "callback draft";
            _control.CursorPosition = 3;
            _control.SelectionAnchor = 1;
            _control.TextComponent.Text = "callback display";
        };
        RequestAndPresent();
        AssertCleared();
        Assert.That(_control.Text, Is.EqualTo("callback draft"));
        Assert.That(_control.CursorPosition, Is.EqualTo(3));
        Assert.That(_control.SelectionAnchor, Is.EqualTo(1));
        Assert.That(_control.TextComponent.Text, Is.EqualTo("callback display"));
    }

    [Test]
    public void RejectedGainRetiresImeWithoutRestoringTheOldDraft()
    {
        UIElementIdentity identity = default;
        long epoch = 0;
        _control.GotFocus += _ =>
        {
            identity = _control.AccessibilityIdentity;
            epoch = _control.FocusEpoch;
            _input.DispatchImeEvents([new WindowImeEvent(WindowImeEventKind.Start, "", 0, identity, epoch)]);
            Assert.That(_control.IsComposing, Is.True);
            Assert.That(_input.ImeCandidateTickRegistered, Is.True);
            Assert.That(_control.UpdateComposition("ime", 3), Is.True);
            _control.TextComponent.Text = "callback composition";
        };
        RequestAndPresent();
        AssertCleared();
        Assert.That(_control.IsComposing, Is.False);
        Assert.That(_input.ImeCandidateTickRegistered, Is.False);
        Assert.That(_control.Text, Is.EqualTo("imeseed"));
        Assert.That(_control.CursorPosition, Is.EqualTo(3));
        Assert.That(_control.TextComponent.Text, Is.EqualTo("callback composition"));
        _input.DispatchImeEvents([new WindowImeEvent(WindowImeEventKind.Commit, "late", 4, identity, epoch)]);
        Assert.That(_control.Text, Is.EqualTo("imeseed"));
        Assert.That(_control.TextComponent.Text, Is.EqualTo("callback composition"));
    }

    [Test]
    public void ANewerFocusRequestOwnsItsRegistrations()
    {
        UIVirtualButtonComponent newer = _root.NewChild("Newer focus").AddComponent<UIVirtualButtonComponent>()!;
        newer.RegisterInputsOnFocus = true;
        _control.GotFocus += _ => _input.FocusedComponent = newer;
        RequestAndPresent();
        Assert.That(_input.FocusedComponent, Is.SameAs(newer));
        Assert.That(_control.IsFocused, Is.False);
        Assert.That(newer.IsFocused, Is.True);
        Assert.That(FocusTargets().GetValueOrDefault(_device), Is.SameAs(newer));
    }

    [Test]
    public void SuccessfulGainAndOrdinaryClearKeepNormalFormatting()
    {
        RequestAndPresent();
        Assert.That(_input.FocusedComponent, Is.SameAs(_control));
        Assert.That(_control.IsFocused, Is.True);
        Assert.That(FocusTargets().GetValueOrDefault(_device), Is.SameAs(_control));
        Assert.That(_control.TextComponent.Text, Is.EqualTo("seed"));
        Assert.That(_input.TryClearFocus(_control, _input.FocusIntentRevision, out ulong cleared), Is.True);
        Assert.That(cleared, Is.Not.Zero);
        AssertCleared();
        Assert.That(_control.TextComponent.Text, Is.EqualTo("[seed]"));
    }

    [Test]
    public void VetoedGainNeverRegistersTheControl()
    {
        _control.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(UITextInputComponent.IsFocused))
                ((XRPropertyChangingEventArgs<bool>)change).AllowChange = false;
        };
        RequestAndPresent();
        AssertCleared();
        Assert.That(_control.TextComponent.Text, Is.EqualTo("[seed]"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AReleaseVetoRetainsFocusWithItsOwnedRegistrations(bool vetoCanvas)
    {
        int gained = 0;
        _control.GotFocus += _ => { gained++; _control.TextComponent.Text = "callback display"; };
        if (vetoCanvas)
            _input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UICanvasInputComponent.FocusedComponent) &&
                    change is XRPropertyChangingEventArgs<UIInteractableComponent?> focus && focus.NewValue is null)
                    focus.AllowChange = false;
            };
        else
            _control.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.IsFocused) &&
                    change is XRPropertyChangingEventArgs<bool> focus && !focus.NewValue)
                    focus.AllowChange = false;
            };
        RequestAndPresent();
        Assert.That(_input.FocusedComponent, Is.SameAs(_control));
        Assert.That(_control.IsFocused, Is.True);
        Assert.That(FocusTargets().GetValueOrDefault(_device), Is.SameAs(_control));
        Assert.That(_control.TextComponent.Text, Is.EqualTo("callback display"));
        Assert.That(gained, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GainExceptionPreservesDisplayAndThePrimaryFailure(bool throwDuringLoss)
    {
        InvalidOperationException original = new("gain observer failed");
        int gained = 0, lost = 0;
        _control.GotFocus += _ =>
        {
            gained++;
            _control.TextComponent.Text = "callback display";
            throw original;
        };
        _control.LostFocus += _ =>
        {
            lost++;
            if (throwDuringLoss) throw new InvalidOperationException("loss observer failed");
        };

        TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(RequestAndPresent)!;
        Assert.That(thrown.InnerException, Is.SameAs(original));
        AssertCleared();
        Assert.That(_control.TextComponent.Text, Is.EqualTo("callback display"));
        Assert.That(gained, Is.EqualTo(1));
        Assert.That(lost, Is.EqualTo(1));
        _input.FocusedComponent = null;
        Assert.That(lost, Is.EqualTo(1));
    }

    [Test]
    public void ANewerEqualControlFocusIntentRetainsItsMappingWithoutReplayingGain()
    {
        int gained = 0;
        _control.GotFocus += _ =>
        {
            gained++;
            _control.TextComponent.Text = "callback display";
            _control.IsFocused = true;
        };
        RequestAndPresent();
        Assert.That(_input.FocusedComponent, Is.SameAs(_control));
        Assert.That(_control.IsFocused, Is.True);
        Assert.That(FocusTargets().GetValueOrDefault(_device), Is.SameAs(_control));
        Assert.That(_control.TextComponent.Text, Is.EqualTo("callback display"));
        Assert.That(gained, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CapturedReleasePreservesDraftCaretDisplayAndDoesNotSubmit(bool commitOnSubmit)
    {
        _control.CommitOnSubmit = commitOnSubmit;
        RequestAndPresent();
        _control.Text = "local draft";
        _control.CursorPosition = 4;
        _control.SelectionAnchor = 2;
        _control.TextComponent.Text = "custom display";
        int lost = 0;
        _control.LostFocus += _ => lost++;
        Assert.That(_input.TryReleaseFocusPreservingDraft(_control, _input.FocusIntentRevision,
            static () => true, out ulong cleared), Is.True);
        Assert.That(cleared, Is.Not.Zero);
        AssertCleared();
        Assert.That((_control.Text, _control.CursorPosition, _control.SelectionAnchor),
            Is.EqualTo(("local draft", 4, 2)));
        Assert.That(_control.TextComponent.Text, Is.EqualTo("custom display"));
        Assert.That(lost, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CanvasClearCallbackPreservesDisplayAndRejectsTheCallerReceipt(bool afterWrite)
    {
        RequestAndPresent();
        bool changed = false;
        void ChangeDisplay()
        {
            if (changed) return;
            changed = true;
            _control.TextComponent.Text = "clear callback display";
        }
        if (afterWrite)
            _input.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(UICanvasInputComponent.FocusedComponent) &&
                    _input.FocusedComponent is null) ChangeDisplay();
            };
        else
            _input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UICanvasInputComponent.FocusedComponent)) ChangeDisplay();
            };

        Assert.That(_input.TryReleaseFocusPreservingDraft(_control, _input.FocusIntentRevision,
            static () => true, out ulong cleared), Is.False);
        Assert.That(cleared, Is.Zero);
        Assert.That(changed, Is.True);
        Assert.That(_control.TextComponent.Text, Is.EqualTo("clear callback display"));
        if (afterWrite) AssertCleared();
        else
        {
            Assert.That(_input.FocusedComponent, Is.SameAs(_control));
            Assert.That(_control.IsFocused, Is.True);
            Assert.That(FocusTargets().GetValueOrDefault(_device), Is.SameAs(_control));
        }
    }

    [TestCase("identity")]
    [TestCase("parent")]
    public void CanvasClearCannotReleaseOrRewriteATransferredControl(string change)
    {
        RequestAndPresent();
        bool changed = false;
        _input.PropertyChanged += (_, property) =>
        {
            if (changed || property.PropertyName != nameof(UICanvasInputComponent.FocusedComponent) ||
                _input.FocusedComponent is not null) return;
            changed = true;
            if (change == "identity") _control.Generate();
            else
            {
                SceneNode parent = _root.NewChild("New owner");
                _row.SetParent(parent.Transform, false, XREngine.Scene.Transforms.EParentAssignmentMode.Immediate);
            }
            _control.TextComponent.Text = "transferred display";
        };
        Assert.That(_input.TryReleaseFocusPreservingDraft(_control, _input.FocusIntentRevision,
            static () => true, out _), Is.False);
        Assert.That(changed, Is.True);
        Assert.That(_input.FocusedComponent, Is.Null);
        Assert.That(_control.IsFocused, Is.True);
        Assert.That(FocusTargets().ContainsKey(_device), Is.False);
        Assert.That(_control.TextComponent.Text, Is.EqualTo("transferred display"));
    }

    [Test]
    public void TransferredControlCannotRetainTheOldHostImeTuple()
    {
        RequestAndPresent();
        UIElementIdentity identity = _control.AccessibilityIdentity;
        long epoch = _control.FocusEpoch;
        _input.DispatchImeEvents([new WindowImeEvent(WindowImeEventKind.Start, "", 0, identity, epoch)]);
        Assert.That(_control.UpdateComposition("ime", 3), Is.True);
        Assert.That(_input.ImeCandidateTickRegistered, Is.True);
        bool changed = false;
        _input.PropertyChanged += (_, property) =>
        {
            if (changed || property.PropertyName != nameof(UICanvasInputComponent.FocusedComponent) ||
                _input.FocusedComponent is not null) return;
            changed = true;
            _control.Generate();
            _control.TextComponent.Text = "transferred display";
        };
        Assert.That(_input.TryReleaseFocusPreservingDraft(_control, _input.FocusIntentRevision,
            static () => true, out _), Is.False);
        Assert.That(_input.ImeCandidateTickRegistered, Is.False);
        Assert.That(_control.IsComposing, Is.True);
        Assert.That(_control.Text, Is.EqualTo("imeseed"));
        _input.DispatchImeEvents([new WindowImeEvent(WindowImeEventKind.Commit, "late", 4, identity, epoch)]);
        Assert.That(_control.Text, Is.EqualTo("imeseed"));
        Assert.That(_control.TextComponent.Text, Is.EqualTo("transferred display"));
        Assert.That(FocusTargets().ContainsKey(_device), Is.False);
    }

    [Test]
    public void ControlLossPrechangeCannotReleaseATransferredControl()
    {
        RequestAndPresent();
        bool changed = false;
        _control.PropertyChanging += (_, property) =>
        {
            if (changed || property.PropertyName != nameof(UITextInputComponent.IsFocused)) return;
            changed = true;
            SceneNode parent = _root.NewChild("New owner");
            _row.SetParent(parent.Transform, false, XREngine.Scene.Transforms.EParentAssignmentMode.Immediate);
            _control.TextComponent.Text = "transferred display";
        };
        Assert.That(_input.TryReleaseFocusPreservingDraft(_control, _input.FocusIntentRevision,
            static () => true, out _), Is.False);
        Assert.That(changed, Is.True);
        Assert.That(_input.FocusedComponent, Is.Null);
        Assert.That(_control.IsFocused, Is.True);
        Assert.That(FocusTargets().ContainsKey(_device), Is.False);
        Assert.That(_control.TextComponent.Text, Is.EqualTo("transferred display"));
    }

    [Test]
    public void ALossCallbackCanSelectANewerTargetWithoutReplayingOldLoss()
    {
        RequestAndPresent();
        UIVirtualButtonComponent newer = _root.NewChild("Newer focus").AddComponent<UIVirtualButtonComponent>()!;
        newer.RegisterInputsOnFocus = true;
        int lost = 0;
        _control.LostFocus += _ =>
        {
            lost++;
            _control.TextComponent.Text = "loss callback display";
            _input.FocusedComponent = newer;
        };
        Assert.That(_input.TryReleaseFocusPreservingDraft(_control, _input.FocusIntentRevision,
            static () => true, out _), Is.False);
        Assert.That(_input.FocusedComponent, Is.SameAs(newer));
        Assert.That(_control.IsFocused, Is.False);
        Assert.That(newer.IsFocused, Is.True);
        Assert.That(FocusTargets().GetValueOrDefault(_device), Is.SameAs(newer));
        Assert.That(_control.TextComponent.Text, Is.EqualTo("loss callback display"));
        Assert.That(lost, Is.EqualTo(1));
    }

    [Test]
    public void CleanupCannotMakeARejectedCompositionAdmissionCurrentAgain()
    {
        RequestAndPresent();
        bool changed = false;
        _input.PropertyChanged += (_, property) =>
        {
            if (changed || property.PropertyName != nameof(UICanvasInputComponent.FocusedComponent) ||
                _input.FocusedComponent is not null) return;
            changed = true;
            Assert.That(_control.BeginComposition(), Is.True);
        };
        string text = _control.Text;
        string? display = _control.TextComponent.Text;
        Assert.That(_input.TryReleaseFocusPreservingDraft(_control, _input.FocusIntentRevision,
            () => !_control.IsComposing, out ulong cleared), Is.False);
        Assert.That(cleared, Is.Zero);
        Assert.That(changed, Is.True);
        AssertCleared();
        Assert.That(_control.IsComposing, Is.False);
        Assert.That(_control.Text, Is.EqualTo(text));
        Assert.That(_control.TextComponent.Text, Is.EqualTo(display));
    }

    [Test]
    public void CallerCallbackCannotReplaceContentBeforeTheRawClear()
    {
        RequestAndPresent();
        bool changed = false;
        Assert.That(_input.TryReleaseFocusPreservingDraft(_control, _input.FocusIntentRevision, () =>
        {
            if (!changed)
            {
                changed = true;
                _control.TextComponent.Text = _control.TextComponent.Text;
            }
            return true;
        }, out _), Is.False);
        Assert.That(changed, Is.True);
        Assert.That(_input.FocusedComponent, Is.SameAs(_control));
        Assert.That(_control.IsFocused, Is.True);
        Assert.That(FocusTargets().GetValueOrDefault(_device), Is.SameAs(_control));
    }

    private void AssertCleared()
    {
        Assert.That(_input.FocusedComponent, Is.Null);
        Assert.That(_control.IsFocused, Is.False);
        Assert.That(FocusTargets().ContainsKey(_device), Is.False);
        Assert.That(typeof(UICanvasInputComponent).GetField("_linkedFocusedComponent",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_input), Is.Null);
    }

    private Dictionary<InputInterface, UIInteractableComponent> FocusTargets()
        => (Dictionary<InputInterface, UIInteractableComponent>)typeof(UICanvasInputComponent)
            .GetField("_focusedInputTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_input)!;

    private void RequestAndPresent()
    {
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _control,
            _input.FocusIntentRevision, static () => true), Is.True);
        BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(100));
        UIHitRecord hit = new(_control.RenderInfo2D, _control.RenderInfo2D.MembershipId,
            _control.UITransform.ElementIdentity, _control.InteractionRevision,
            Matrix4x4.Identity, new Vector2(20), bounds, null, 3, 2, 4, 3, 0, 0, true, false,
            FocusScopeIdentity: _canvas.CanvasTransform.ElementIdentity,
            VirtualListIdentity: _list.ElementIdentity, VirtualRowIdentity: _row.ElementIdentity,
            VirtualRowLease: _lease, VirtualProviderIncarnation: _list.VirtualProviderIncarnation);
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
