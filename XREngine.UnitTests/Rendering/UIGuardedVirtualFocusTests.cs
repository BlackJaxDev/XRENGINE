using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIGuardedVirtualFocusTests
{
    private UITestShaderScope _shaders = null!;
    private SceneNode _root = null!;
    private UICanvasComponent _canvas = null!;
    private UICanvasInputComponent _input = null!;
    private UIListTransform _list = null!;
    private UIVirtualFocusCallbackProvider _provider = null!;
    private UIBoundableTransform _row = null!;
    private UIVirtualRowLease _lease;
    private UIVirtualButtonComponent _button = null!;

    [SetUp]
    public void SetUp()
    {
        _shaders = new UITestShaderScope();
        _root = new SceneNode("Guarded focus", new UICanvasTransform());
        _root.SetWorldContext(new TestWorldContext());
        _canvas = _root.AddComponent<UICanvasComponent>()!;
        _input = _root.AddComponent<UICanvasInputComponent>()!;
        _list = new UIListTransform { ItemSize = 20, VirtualOverscan = 0 };
        SceneNode listNode = new(_root, "List", _list);
        UIVirtualTreeProvider provider = new(
            () =>
            {
                UIBoundableTransform row = new();
                SceneNode node = new(listNode, "Row", row);
                node.AddComponent<UIVirtualButtonComponent>();
                return row;
            }, (_, _, _, _) => { }, (_, _, _) => { });
        provider.Insert(1, 0, 0, "First");
        _provider = new UIVirtualFocusCallbackProvider(provider);
        _list.VirtualProvider = _provider;
        _list.SynchronizeVirtualRows(20);
        Assert.That(_list.TryGetRealizedVirtualRow(1, out UIBoundableTransform? row,
            out _lease), Is.True);
        _row = row!;
        _button = row!.SceneNode!.GetComponent<UIVirtualButtonComponent>()!;
    }

    [TearDown]
    public void TearDown()
    {
        _root?.Destroy(true);
        _shaders?.Dispose();
    }

    [Test]
    public void FalseInitialProofDoesNotTakeAnIntentOrInstallPendingFocus()
    {
        ulong before = _input.FocusIntentRevision;
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            before, static () => false), Is.False);
        Assert.That(_input.FocusIntentRevision, Is.EqualTo(before));
        Assert.That(_input.HasPendingFocusRequest, Is.False);
    }

    [Test]
    public void InputSourceWithoutGuardedImplementationRejectsTheOwnedRequest()
    {
        IUICanvasInputSource source = new UITestCanvasInputProbe { VirtualFocusAccepted = true };
        Assert.That(source.RequestVirtualItemFocus(_list, 1, _button,
            source.FocusIntentRevision, static () => true), Is.False);
        Assert.That(source.TryClearFocus(_button, source.FocusIntentRevision,
            out ulong cleared), Is.False);
        Assert.That(cleared, Is.Zero);
    }

    [Test]
    public void StaleExpectedIntentRejectsBeforeTheScrollRequest()
    {
        ulong stale = _input.FocusIntentRevision;
        _input.FocusedComponent = null;
        ulong newer = _input.FocusIntentRevision;
        Assert.That(newer, Is.Not.EqualTo(stale));
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            stale, static () => true), Is.False);
        Assert.That(_input.FocusIntentRevision, Is.EqualTo(newer));
        Assert.That(_input.HasPendingFocusRequest, Is.False);
    }

    [Test]
    public void ProofLossWhilePendingCannotFocusThePresentedRow()
    {
        bool current = true;
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            _input.FocusIntentRevision, () => current), Is.True);
        current = false;
        Present();
        Assert.That(_input.FocusedComponent, Is.Null);
        Assert.That(_button.IsFocused, Is.False);
        Assert.That(_input.HasPendingFocusRequest, Is.False);
    }

    [Test]
    public void ExplicitEqualNullFocusIntentCancelsPendingRequest()
    {
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            _input.FocusIntentRevision, static () => true), Is.True);
        ulong pendingIntent = _input.FocusIntentRevision;
        _input.FocusedComponent = null;
        Assert.That(_input.FocusIntentRevision, Is.GreaterThan(pendingIntent));
        Present();
        Assert.That(_input.FocusedComponent, Is.Null);
        Assert.That(_button.IsFocused, Is.False);
    }

    [Test]
    public void ExactTargetAdmissionRejectsAnotherPresentedTabStop()
    {
        UIVirtualButtonComponent other = _root.NewChild("Another control")
            .AddComponent<UIVirtualButtonComponent>()!;
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, other,
            _input.FocusIntentRevision, static () => true), Is.True);
        Present();
        Assert.That(_input.FocusedComponent, Is.Null);
        Assert.That(_button.IsFocused, Is.False);
    }

    [Test]
    public void GuardedRequestFocusesOnlyItsExactPresentedTarget()
    {
        bool admittedInCallback = false;
        _button.GotFocus += _ =>
            admittedInCallback = _input.IsDispatchingVirtualItemFocus(_list, 1, _button);
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            _input.FocusIntentRevision, static () => true), Is.True);
        Present();
        Assert.That(_input.FocusedComponent, Is.SameAs(_button));
        Assert.That(admittedInCallback, Is.True);
    }

    [Test]
    public void ExactClearNeedsTheCapturedTargetAndIntent()
    {
        _input.FocusedComponent = _button;
        ulong original = _input.FocusIntentRevision;
        UIVirtualButtonComponent other = _root.NewChild("Other")
            .AddComponent<UIVirtualButtonComponent>()!;
        Assert.That(_input.TryClearFocus(other, original, out _), Is.False);
        Assert.That(_input.TryClearFocus(_button, original - 1, out _), Is.False);
        Assert.That(_input.FocusedComponent, Is.SameAs(_button));
        Assert.That(_input.TryClearFocus(_button, original, out ulong cleared), Is.True);
        Assert.That(cleared, Is.EqualTo(_input.FocusIntentRevision));
        Assert.That(_input.FocusedComponent, Is.Null);
        Assert.That(_input.TryClearFocus(_button, original, out _), Is.False);
    }

    [Test]
    public void NestedFocusInClearChangingKeepsTheNewerTarget()
    {
        _input.FocusedComponent = _button;
        ulong original = _input.FocusIntentRevision;
        UIVirtualButtonComponent newer = _root.NewChild("New focus")
            .AddComponent<UIVirtualButtonComponent>()!;
        bool nested = false;
        _input.PropertyChanging += (_, change) =>
        {
            if (nested || change.PropertyName != nameof(UICanvasInputComponent.FocusedComponent)) return;
            nested = true;
            _input.FocusedComponent = newer;
        };
        Assert.That(_input.TryClearFocus(_button, original, out ulong cleared), Is.False);
        Assert.That(cleared, Is.Zero);
        Assert.That(_input.FocusedComponent, Is.SameAs(newer));
    }

    [Test]
    public void VetoedExactClearCannotIssueACompletionReceipt()
    {
        _input.FocusedComponent = _button;
        ulong original = _input.FocusIntentRevision;
        _input.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(UICanvasInputComponent.FocusedComponent))
                ((XRPropertyChangingEventArgs<UIInteractableComponent?>)change).AllowChange = false;
        };
        Assert.That(_input.TryClearFocus(_button, original, out ulong cleared), Is.False);
        Assert.That(cleared, Is.Zero);
        Assert.That(_input.FocusedComponent, Is.SameAs(_button));
    }

    [Test]
    public void VetoedExactClearRetainsThePreviousKeyboardRegistration()
    {
        FocusRegistrationProbe previous = _root.NewChild("Keyboard focus")
            .AddComponent<FocusRegistrationProbe>()!;
        previous.RegisterInputsOnFocus = true;
        XREngine.Input.Devices.LocalInputInterface device = new();
        _input.RegisterInput(device);
        _input.FocusedComponent = previous;
        Assert.That(previous.Registrations, Is.EqualTo(1));
        Assert.That(previous.Removals, Is.Zero);
        ulong original = _input.FocusIntentRevision;
        previous.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(FocusRegistrationProbe.IsFocused) &&
                change is XRPropertyChangingEventArgs<bool> focus && !focus.NewValue)
                focus.AllowChange = false;
        };

        Assert.That(_input.TryClearFocus(previous, original, out ulong cleared), Is.False);
        Assert.That(cleared, Is.Zero);
        Assert.That(_input.FocusedComponent, Is.SameAs(previous));
        Assert.That(previous.IsFocused, Is.True);
        Assert.That(previous.Registrations, Is.EqualTo(1));
        Assert.That(previous.Removals, Is.Zero);
        Assert.That(device.Unregister, Is.False);
    }

    [Test]
    public void TargetRegenerationDuringClearPrechangeRetainsTheOldFocusField()
    {
        _input.FocusedComponent = _button;
        ulong original = _input.FocusIntentRevision;
        bool regenerated = false;
        _input.PropertyChanging += (_, change) =>
        {
            if (regenerated || change.PropertyName != nameof(UICanvasInputComponent.FocusedComponent))
                return;
            regenerated = true;
            _button.Generate();
        };
        Assert.That(_input.TryClearFocus(_button, original, out ulong cleared), Is.False);
        Assert.That(regenerated, Is.True);
        Assert.That(cleared, Is.Zero);
        Assert.That(_input.FocusedComponent, Is.SameAs(_button));
    }

    [Test]
    public void NestedFocusInGuardedChangingCannotBeOverwritten()
    {
        UIVirtualButtonComponent newer = _root.NewChild("New focus")
            .AddComponent<UIVirtualButtonComponent>()!;
        bool nested = false;
        _input.PropertyChanging += (_, change) =>
        {
            if (nested || change.PropertyName != nameof(UICanvasInputComponent.FocusedComponent)) return;
            nested = true;
            _input.FocusedComponent = newer;
        };
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            _input.FocusIntentRevision, static () => true), Is.True);
        Present();
        Assert.That(nested, Is.True);
        Assert.That(_input.FocusedComponent, Is.SameAs(newer));
        Assert.That(_button.IsFocused, Is.False);
    }

    [Test]
    public void TargetFocusPrechangeProofLossCannotWriteIsFocusedOrInvokeGotFocus()
    {
        bool current = true;
        int gotFocus = 0;
        _button.GotFocus += _ => gotFocus++;
        _button.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(UIVirtualButtonComponent.IsFocused))
                current = false;
        };
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            _input.FocusIntentRevision, () => current), Is.True);
        Present();
        Assert.That(current, Is.False);
        Assert.That(_button.IsFocused, Is.False);
        Assert.That(gotFocus, Is.Zero);
        Assert.That(_input.FocusedComponent, Is.Null);
    }

    [Test]
    public void VetoedTargetFocusCannotLeaveAnAcceptedFocusOrRegistration()
    {
        int gotFocus = 0;
        _button.GotFocus += _ => gotFocus++;
        _button.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(UIVirtualButtonComponent.IsFocused))
                ((XRPropertyChangingEventArgs<bool>)change).AllowChange = false;
        };
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            _input.FocusIntentRevision, static () => true), Is.True);
        Present();
        Assert.That(gotFocus, Is.Zero);
        Assert.That(_button.IsFocused, Is.False);
        Assert.That(_input.FocusedComponent, Is.Null);
    }

    [Test]
    public void VetoedPreviousReleaseCannotActivateTheNewTarget()
    {
        UIVirtualButtonComponent previous = _root.NewChild("Previous focus")
            .AddComponent<UIVirtualButtonComponent>()!;
        _input.FocusedComponent = previous;
        Assert.That(previous.IsFocused, Is.True);
        previous.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(UIVirtualButtonComponent.IsFocused) &&
                change is XRPropertyChangingEventArgs<bool> focus && !focus.NewValue)
                focus.AllowChange = false;
        };
        int gotFocus = 0;
        _button.GotFocus += _ => gotFocus++;
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            _input.FocusIntentRevision, static () => true), Is.True);
        Present();
        Assert.That(previous.IsFocused, Is.True);
        Assert.That(_button.IsFocused, Is.False);
        Assert.That(gotFocus, Is.Zero);
        Assert.That(_input.FocusedComponent, Is.SameAs(previous));
    }

    [Test]
    public void RegeneratedPreviousControlCannotBeReleasedByOldDispatch()
    {
        UIVirtualButtonComponent previous = _root.NewChild("Previous lifetime")
            .AddComponent<UIVirtualButtonComponent>()!;
        _input.FocusedComponent = previous;
        bool regenerated = false;
        previous.PropertyChanging += (_, change) =>
        {
            if (regenerated || change.PropertyName != nameof(UIVirtualButtonComponent.IsFocused))
                return;
            regenerated = true;
            previous.Generate();
        };
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            _input.FocusIntentRevision, static () => true), Is.True);
        Present();
        Assert.That(regenerated, Is.True);
        Assert.That(previous.IsFocused, Is.True);
        Assert.That(_input.FocusedComponent, Is.SameAs(previous));
        Assert.That(_button.IsFocused, Is.False);
    }

    [Test]
    public void PreviousFocusPrechangeReselectionKeepsItsNewerIntent()
    {
        UIVirtualButtonComponent previous = _root.NewChild("Reselected focus")
            .AddComponent<UIVirtualButtonComponent>()!;
        _input.FocusedComponent = previous;
        bool reselected = false;
        previous.PropertyChanging += (_, change) =>
        {
            if (reselected || change.PropertyName != nameof(UIVirtualButtonComponent.IsFocused))
                return;
            reselected = true;
            _input.FocusedComponent = previous;
        };
        ulong before = _input.FocusIntentRevision;
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            before, static () => true), Is.True);
        Present();
        Assert.That(reselected, Is.True);
        Assert.That(_input.FocusIntentRevision, Is.GreaterThan(before + 1));
        Assert.That(_input.FocusedComponent, Is.SameAs(previous));
        Assert.That(previous.IsFocused, Is.True);
        Assert.That(_button.IsFocused, Is.False);
    }

    [Test]
    public void TargetRegenerationDuringFocusChangingCannotCommitOldRequest()
    {
        bool regenerated = false;
        _input.PropertyChanging += (_, change) =>
        {
            if (regenerated || change.PropertyName != nameof(UICanvasInputComponent.FocusedComponent))
                return;
            regenerated = true;
            _button.Generate();
        };
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            _input.FocusIntentRevision, static () => true), Is.True);
        Present();
        Assert.That(regenerated, Is.True);
        Assert.That(_input.FocusedComponent, Is.Null);
        Assert.That(_button.IsFocused, Is.False);
    }

    [Test]
    public void ProviderQueryAfterInputChangingCannotCommitAStaleProof()
    {
        bool current = true;
        bool armed = false;
        bool invalidated = false;
        _input.PropertyChanging += (_, change) =>
        {
            if (armed || change.PropertyName != nameof(UICanvasInputComponent.FocusedComponent))
                return;
            armed = true;
            _provider.OnGetItemId = () =>
            {
                if (invalidated) return;
                invalidated = true;
                current = false;
            };
        };
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            _input.FocusIntentRevision, () => current), Is.True);
        Present();
        Assert.That(armed, Is.True);
        Assert.That(invalidated, Is.True);
        Assert.That(_input.FocusedComponent, Is.Null);
        Assert.That(_button.IsFocused, Is.False);
    }

    [Test]
    public void FocusChangingReplacementRequestSurvivesOldDispatchFailure()
    {
        bool nested = false;
        _input.PropertyChanging += (_, change) =>
        {
            if (nested || change.PropertyName != nameof(UICanvasInputComponent.FocusedComponent))
                return;
            nested = true;
            Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
                _input.FocusIntentRevision, static () => true), Is.True);
        };
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            _input.FocusIntentRevision, static () => true), Is.True);
        Present();
        Assert.That(nested, Is.True);
        Assert.That(_input.FocusedComponent, Is.Null);
        Assert.That(_input.HasPendingFocusRequest, Is.True);
    }

    [Test]
    public void FocusPostchangeNestedIntentWinsOverAcceptedVirtualRequest()
    {
        UIVirtualButtonComponent newer = _root.NewChild("Postchange owner")
            .AddComponent<UIVirtualButtonComponent>()!;
        bool nested = false;
        _input.PropertyChanged += (_, change) =>
        {
            if (nested || change.PropertyName != nameof(UICanvasInputComponent.FocusedComponent))
                return;
            nested = true;
            _input.FocusedComponent = newer;
        };
        Assert.That(_input.RequestVirtualItemFocus(_list, 1, _button,
            _input.FocusIntentRevision, static () => true), Is.True);
        Present();
        Assert.That(nested, Is.True);
        Assert.That(_input.FocusedComponent, Is.SameAs(newer));
        Assert.That(_button.IsFocused, Is.False);
    }

    [Test]
    public void ExactClearPostchangeNewIntentReconcilesTheNewFocus()
    {
        _input.FocusedComponent = _button;
        ulong original = _input.FocusIntentRevision;
        UIVirtualButtonComponent newer = _root.NewChild("Newer focus")
            .AddComponent<UIVirtualButtonComponent>()!;
        bool nested = false;
        _button.PropertyChanged += (_, change) =>
        {
            if (nested || change.PropertyName != nameof(UIVirtualButtonComponent.IsFocused) ||
                _button.IsFocused)
                return;
            nested = true;
            _input.FocusedComponent = newer;
        };
        Assert.That(_input.TryClearFocus(_button, original, out ulong cleared), Is.False);
        Assert.That(cleared, Is.Zero);
        Assert.That(nested, Is.True);
        Assert.That(_input.FocusedComponent, Is.SameAs(newer));
        Assert.That(newer.IsFocused, Is.True);
    }

    private void Present()
    {
        BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(100));
        UIHitRecord hit = new(_button.RenderInfo2D,
            _button.RenderInfo2D.MembershipId,
            _button.UITransform.ElementIdentity, _button.InteractionRevision,
            Matrix4x4.Identity, new Vector2(20), bounds, null,
            3, 2, 4, 3, 0, 0, true, false,
            FocusScopeIdentity: _canvas.CanvasTransform.ElementIdentity,
            VirtualListIdentity: _list.ElementIdentity,
            VirtualRowIdentity: _row.ElementIdentity,
            VirtualRowLease: _lease,
            VirtualProviderIncarnation: _list.VirtualProviderIncarnation);
        UIPublication packet = new(1, _canvas.PublicationLifetime, 1, 1,
            _canvas.CanvasTransform.ChangeJournal.Revision, default,
            new UIElementIdentity[] { _canvas.CanvasTransform.ElementIdentity,
                _list.ElementIdentity, _row.ElementIdentity,
                _button.UITransform.ElementIdentity }, [hit], [], bounds,
            Matrix4x4.Identity, Matrix4x4.Identity);
        _canvas.MarkPresented(_canvas, packet);
        packet.Release();
        UIPublication? read = (UIPublication?)typeof(UICanvasInputComponent)
            .GetMethod("RefreshPresentedInput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_input, null);
        read?.Release();
    }
}
