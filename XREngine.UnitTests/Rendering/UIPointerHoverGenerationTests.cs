using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks hover callbacks against replacement presentations and owner retirement.</summary>
[TestFixture, NonParallelizable]
public sealed class UIPointerHoverGenerationTests
{
    private UITestShaderScope _shaderScope = null!;
    private SceneNode _root = null!;
    private UICanvasComponent _canvas = null!;
    private UICanvasInputComponent _input = null!;
    private UIVirtualButtonComponent _first = null!;
    private UIVirtualButtonComponent _second = null!;
    private Action _collect = null!;
    private Action _swap = null!;
    private ulong _generation;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [SetUp]
    public void SetUp()
    {
        _shaderScope = new UITestShaderScope();
        _root = new SceneNode("Hover generations", new UICanvasTransform());
        _root.SetWorldContext(new TestWorldContext());
        _canvas = _root.AddComponent<UICanvasComponent>()!;
        _input = _root.AddComponent<UICanvasInputComponent>()!;
        _first = _root.NewChild("First").AddComponent<UIVirtualButtonComponent>()!;
        _second = _root.NewChild("Second").AddComponent<UIVirtualButtonComponent>()!;
        _collect = typeof(UICanvasInputComponent).GetMethod("CollectVisible", PrivateInstance)!
            .CreateDelegate<Action>(_input);
        _swap = typeof(UICanvasInputComponent).GetMethod("SwapBuffers", PrivateInstance)!
            .CreateDelegate<Action>(_input);
        typeof(UICanvasInputComponent).GetMethod("MouseMove", PrivateInstance)!
            .CreateDelegate<Action<float, float>>(_input)(10, 10);
        _generation = 0;
    }

    [TearDown]
    public void TearDown()
    {
        try { _root.Destroy(true); }
        finally { _shaderScope.Dispose(); }
    }

    [Test]
    public void ReplacementBetweenCollectAndSwapUsesTheNewHits()
    {
        Publish(_first);
        _collect();
        Publish(_second);
        Refresh();
        Assert.DoesNotThrow(_swap.Invoke);
        Assert.That(_input.TopMostInteractable, Is.SameAs(_second));
        Assert.That(_first.IsMouseOver, Is.False);
        Assert.That(_second.IsMouseOver, Is.True);
    }

    [Test]
    public void EnterCallbackReplacementCannotDispatchTheOldDirectHoverOrLoseCleanup()
    {
        _first.MouseOverlapEnter += _ =>
        {
            Publish(_second);
            Refresh();
            _collect();
            _swap();
        };
        Publish(_first);
        Query();
        Assert.That(_input.TopMostInteractable, Is.Null);
        Assert.That(_first.IsMouseDirectlyOver, Is.False);
        Assert.That(_second.IsMouseOver, Is.False);
        Query();
        Assert.That(_input.TopMostInteractable, Is.SameAs(_second));
        Assert.That(_first.IsMouseOver, Is.False);
        Assert.That(_second.IsMouseDirectlyOver, Is.True);
    }

    [Test]
    public void ThrowingEnterKeepsItsCleanupOwnerForTheNextQuery()
    {
        _first.MouseOverlapEnter += _ => throw new InvalidOperationException("Hover callback failure");
        Publish(_first);
        _collect();
        Assert.Throws<InvalidOperationException>(_swap.Invoke);
        Publish(_second);
        Query();
        Assert.That(_first.IsMouseOver, Is.False);
        Assert.That(_second.IsMouseOver, Is.True);
        Assert.That(_input.TopMostInteractable, Is.SameAs(_second));
    }

    [Test]
    public void RecycledTargetBetweenCollectionAndDispatchCannotBlockCamera()
    {
        Publish(_first);
        _collect();
        _first.CancelForRecycle();
        _swap();
        Assert.That(_input.TopMostInteractable, Is.Null);
        Assert.That(_input.IsPointerBlockingCamera(), Is.False);
        Assert.That(_first.IsMouseOver, Is.False);
    }

    [Test]
    public void CameraBlockingUsesDisplayedGeometryUntilAReplacementIsPresented()
    {
        FlyingCameraPawnComponent camera = new();
        camera.LinkedUICanvasInputs.Add(_input);
        try
        {
            Publish(_first);
            Assert.That(camera.IsHoveringUI(), Is.True);
            Query();
            _first.UITransform.Translation = new Vector2(500, 500);
            Assert.That(camera.IsHoveringUI(), Is.True);
            Publish(null);
            Assert.That(camera.IsHoveringUI(), Is.False);
        }
        finally { camera.Destroy(true); }
    }

    [Test]
    public void MenuPointUsesThePresentedTargetAndRejectsReplacementMembership()
    {
        Publish(_first);
        _first.UITransform.Translation = new Vector2(500, 500);
        Assert.That(_input.TryGetPresentedPointerPosition(_first, out Vector2 point), Is.True);
        Assert.That(point, Is.EqualTo(new Vector2(10, 10)));
        Publish(_second);
        Assert.That(_input.TryGetPresentedPointerPosition(_first, out _), Is.False);
        Assert.That(_input.TryGetPresentedPointerPosition(_second, out point), Is.True);
        Assert.That(point, Is.EqualTo(new Vector2(10, 10)));
        _second.CancelForRecycle();
        Assert.That(_input.TryGetPresentedPointerPosition(_second, out _), Is.False);
    }

    [Test]
    public void FocusCallbackReplacementDoesNotReturnTheRetiredOuterPacket()
    {
        Publish(_second);
        Query();
        int callbacks = 0;
        _first.GotFocus += _ =>
        {
            callbacks++;
            Publish(_second, useCanvasScope: true);
            Refresh();
        };
        Publish(_first, useCanvasScope: true);
        UIPublication? result = (UIPublication?)typeof(UICanvasInputComponent)
            .GetMethod("RefreshPresentedInput", PrivateInstance)!.Invoke(_input, null);
        try { Assert.That(result, Is.Null); }
        finally { result?.Release(); }
        Assert.That(callbacks, Is.EqualTo(1));
        Query();
        Assert.That(_input.TopMostInteractable, Is.SameAs(_second));
    }

    [Test]
    public void FocusChangeDuringCollectionCannotReenterHitCollection()
    {
        bool countersEnabled = UIRuntimeCounters.Enabled;
        try
        {
            UIRuntimeCounters.Enabled = true;
            Publish(_first);
            Query();
            _input.FocusedComponent = _first;
            int callbacks = 0;
            _first.LostFocus += _ =>
            {
                callbacks++;
                _collect();
                _swap();
            };
            Publish(_second, useCanvasScope: true);
            long before = UIRuntimeCounters.Read(UIWorkCounter.HitQueries);
            Query();
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(UIRuntimeCounters.Read(UIWorkCounter.HitQueries) - before, Is.EqualTo(1));
            Assert.That(_input.TopMostInteractable, Is.SameAs(_second));
            Assert.That(_first.IsMouseOver, Is.False);
            Assert.That(_second.IsMouseOver, Is.True);
        }
        finally { UIRuntimeCounters.Enabled = countersEnabled; }
    }

    [Test]
    public void PresentedDecorativeModalBlocksCameraWithoutAnInteractableChild()
    {
        UIMaterialComponent backdrop = _root.NewChild("Modal backdrop").AddComponent<UIMaterialComponent>()!;
        BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(100));
        UIHitRecord hit = new(backdrop.RenderInfo2D, backdrop.RenderInfo2D.MembershipId,
            backdrop.UITransform.ElementIdentity, 0, Matrix4x4.Identity, new Vector2(100),
            bounds, null, 1, 0, 2, 1, 0, 0, false, true);
        UIPublication packet = new(1, _canvas.PublicationLifetime, 1, 1, 1,
            default, default, [hit], [], bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        try { _canvas.MarkPresented(_canvas, packet); }
        finally { packet.Release(); }
        Assert.That(_input.IsPointerBlockingCamera(), Is.True);
        Assert.That(_input.TopMostInteractable, Is.Null);
        backdrop.IsActive = false;
        Assert.That(_input.IsPointerBlockingCamera(), Is.False);
    }

    private void Query() { _collect(); _swap(); }

    private void Refresh()
    {
        UIPublication? publication = (UIPublication?)typeof(UICanvasInputComponent)
            .GetMethod("RefreshPresentedInput", PrivateInstance)!.Invoke(_input, null);
        publication?.Release();
    }

    private void Publish(UIVirtualButtonComponent? target, bool useCanvasScope = false)
    {
        BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(100));
        UIHitRecord[] hits = target is null ? [] :
        [new(target.RenderInfo2D, target.RenderInfo2D.MembershipId,
            target.UITransform.ElementIdentity, target.InteractionRevision,
            Matrix4x4.Identity, new Vector2(100), bounds, null,
            1, 0, 2, 1, 0, 0, true, false,
            FocusScopeIdentity: useCanvasScope ? _canvas.CanvasTransform.ElementIdentity : default)];
        UIPublication publication = new(++_generation, _canvas.PublicationLifetime,
            _generation, _generation, (long)_generation, default,
            useCanvasScope ? new UIElementIdentity[] { _canvas.CanvasTransform.ElementIdentity } : default, hits, [],
            bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        try { _canvas.MarkPresented(_canvas, publication); }
        finally { publication.Release(); }
    }
}
