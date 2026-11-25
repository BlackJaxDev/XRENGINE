using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks drop regions against the displayed input packet.</summary>
[TestFixture, NonParallelizable]
public sealed class UIPresentedDropRegionTests
{
    private UITestShaderScope _shaders = null!;
    private SceneNode _root = null!;
    private UICanvasComponent _canvas = null!;
    private UICanvasInputComponent _input = null!;
    private UIVirtualButtonComponent _region = null!;
    private UIVirtualButtonComponent _other = null!;
    private ulong _generation;
    private static readonly BoundingRectangleF Bounds = new(Vector2.Zero, new Vector2(100));

    [SetUp]
    public void SetUp()
    {
        _shaders = new UITestShaderScope();
        _root = new SceneNode("Drop regions", new UICanvasTransform());
        _root.SetWorldContext(new TestWorldContext());
        _canvas = _root.AddComponent<UICanvasComponent>()!;
        _input = _root.AddComponent<UICanvasInputComponent>()!;
        _region = _root.NewChild("Region").AddComponent<UIVirtualButtonComponent>()!;
        _region.IsEnabled = false;
        _region.IsTabStop = false;
        _region.AccessibilityHidden = true;
        _other = _root.NewChild("Other").AddComponent<UIVirtualButtonComponent>()!;
        _generation = 0;
    }

    [TearDown]
    public void TearDown()
    {
        try { _root.Destroy(true); }
        finally { _shaders.Dispose(); }
    }

    [Test]
    public void DisabledRegionUsesPresentedShapeWithoutChangingOrdinaryInput()
    {
        Publish(Region());
        _region.UITransform.Translation = new Vector2(500);
        Assert.That(Query(new Vector2(20), out Vector2 point), Is.True);
        Assert.That(point, Is.EqualTo(new Vector2(20)));
        Assert.That(_region.CanInteract, Is.False);
        Assert.That(_input.IsPointerBlockingCamera(), Is.False);
        Assert.That(Query(new Vector2(500), out _), Is.False);
    }

    [Test]
    public void UsesTheMappingAndClipFromTheSamePacket()
    {
        UIHitRecord region = Region() with
        {
            Clip = new BoundingRectangle(10, 10, 20, 20),
        };
        PublishWithMapping(Matrix4x4.CreateScale(0.5f), region);
        Assert.That(Query(new Vector2(30), out Vector2 point), Is.True);
        Assert.That(point, Is.EqualTo(new Vector2(15)));
        Assert.That(Query(new Vector2(70), out _), Is.False);
    }

    [Test]
    public void FrozenLocalShapeRejectsPointsInsideOnlyItsBoundingBox()
    {
        Publish(Region() with { CanvasToLocal = Matrix4x4.CreateTranslation(90, 0, 0) });
        Assert.That(Query(new Vector2(20), out _), Is.False);
    }

    [TestCase(99f, 100f)]
    [TestCase(100f, 101f)]
    [TestCase(0f, 100f)]
    [TestCase(float.NaN, 100f)]
    public void RejectsMismatchedOrInvalidNativeExtent(float width, float height)
    {
        Publish(Region());
        Assert.That(_input.TryGetPresentedDropRegion(_region, new Vector2(20),
            new Vector2(width, height), out _), Is.False);
        Assert.That(_input.TryGetPresentedDropRegion(_region, new Vector2(20),
            new Vector2(100), out _), Is.True);
    }

    [Test]
    public void MissingOrRecycledRegionCannotUseOldGeometry()
    {
        Publish(Region());
        Assert.That(Query(new Vector2(20), out _), Is.True);
        _region.CancelForRecycle();
        Assert.That(Query(new Vector2(20), out _), Is.False);
        Publish(Region());
        Assert.That(Query(new Vector2(20), out _), Is.True);
        Publish();
        Assert.That(Query(new Vector2(20), out _), Is.False);
    }

    [Test]
    public void ScopeMismatchRejectsOtherwiseCurrentRegion()
    {
        Publish(Region() with { FocusScopeIdentity = _other.UITransform.ElementIdentity });
        Assert.That(Query(new Vector2(20), out _), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FocusCallbackCannotReplaceThePresentedPacketDuringAQuery(bool clear)
    {
        Publish(Region());
        Assert.That(Query(new Vector2(20), out _), Is.True);
        int callbacks = 0;
        _other.GotFocus += _ =>
        {
            callbacks++;
            if (clear)
                _canvas.ClearPresentedPublication(_canvas);
            else
                Publish();
        };
        PublishInCanvasScope();
        Assert.That(Query(new Vector2(20), out _), Is.False);
        Assert.That(callbacks, Is.EqualTo(1));
    }

    [TestCase("input")]
    [TestCase("canvas")]
    [TestCase("region")]
    [TestCase("node")]
    [TestCase("transform")]
    public void FocusCallbackCannotReplaceAQueryOwnerLifetime(string owner)
    {
        Publish(Region());
        Assert.That(Query(new Vector2(20), out _), Is.True);
        XRObjectBase target = owner switch
        {
            "input" => _input,
            "canvas" => _canvas,
            "region" => _region,
            "node" => _region.SceneNode,
            _ => _region.UITransform,
        };
        int callbacks = 0;
        _other.GotFocus += _ =>
        {
            callbacks++;
            Guid originalId = target.ID;
            target.AdoptPersistentID(Guid.NewGuid());
            target.AdoptPersistentID(originalId);
        };
        PublishInCanvasScope();
        Assert.That(Query(new Vector2(20), out _), Is.False);
        Assert.That(callbacks, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnrelatedInputOrBlockerOccludesRegion(bool blocks)
    {
        Publish(Region(), Other() with { BlocksInputBehind = blocks });
        Assert.That(Query(new Vector2(20), out _), Is.False);
    }

    [Test]
    public void PresentedChildInsideRegionRemainsAnEligibleDrop()
    {
        Publish(Region(), Other() with { NodeIndex = 2, ParentIndex = 1, SubtreeEndIndex = 3 });
        Assert.That(Query(new Vector2(20), out _), Is.True);
    }

    [Test]
    public void AncestorBlockerAllowsItsOwnRegionButChildBlockerDoesNot()
    {
        UIHitRecord ancestor = Other() with
        {
            NodeIndex = 0, SubtreeEndIndex = 10, Depth = 0, IsInteractable = false, BlocksInputBehind = true,
        };
        Publish(Region(), ancestor);
        Assert.That(Query(new Vector2(20), out _), Is.True);
        Publish(Region(), ancestor with { NodeIndex = 2, ParentIndex = 1, SubtreeEndIndex = 3, Depth = 2 });
        Assert.That(Query(new Vector2(20), out _), Is.False);
    }

    [Test]
    public void QueuedRegionAndHiddenCanvasRejectDisplayedGeometry()
    {
        Publish(Region());
        _canvas.CanvasTransform.Hide();
        Assert.That(Query(new Vector2(20), out _), Is.False);
        _canvas.CanvasTransform.Show();
        _region.Destroy(false);
        Assert.That(Query(new Vector2(20), out _), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void QueryDoesNotConvertAReplacedRegionOrBlockerTransform(bool replaceBlocker)
    {
        Publish(Region(), Other() with { BlocksInputBehind = true });
        SceneNode node = (replaceBlocker ? _other : _region).SceneNode;
        TransformBase original = node.Transform;
        Transform replacement = new();
        // A property callback can read the new field before transform relinking finishes.
        FieldInfo field = typeof(SceneNode).GetField("_transform", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            field.SetValue(node, replacement);
            Assert.That(node.Transform, Is.SameAs(replacement));
            Assert.That(Query(new Vector2(20), out _), Is.EqualTo(replaceBlocker));
            Assert.That(node.Transform, Is.SameAs(replacement));
        }
        finally
        {
            field.SetValue(node, original);
            replacement.Destroy(true);
        }
    }

    [TestCase(float.NaN, 20f)]
    [TestCase(20f, float.PositiveInfinity)]
    [TestCase(-1f, 20f)]
    [TestCase(100f, 20f)]
    public void RejectsInvalidOrOutsideClientPoints(float x, float y)
    {
        Publish(Region());
        Assert.That(Query(new Vector2(x, y), out _), Is.False);
    }

    private bool Query(Vector2 point, out Vector2 canvasPoint)
        => _input.TryGetPresentedDropRegion(_region, point, out canvasPoint);

    private UIHitRecord Region()
        => new(_region.RenderInfo2D, _region.RenderInfo2D.MembershipId,
            _region.UITransform.ElementIdentity, _region.InteractionRevision,
            Matrix4x4.Identity, new Vector2(100), Bounds, null, 1, 0, 4, 1, 0, 0, false, false);

    private UIHitRecord Other()
        => new(_other.RenderInfo2D, _other.RenderInfo2D.MembershipId,
            _other.UITransform.ElementIdentity, _other.InteractionRevision,
            Matrix4x4.Identity, new Vector2(100), Bounds, null, 5, 0, 6, 2, 0, 1, true, false);

    private void Publish(params UIHitRecord[] hits) => PublishWithMapping(Matrix4x4.Identity, hits);

    private void PublishInCanvasScope()
    {
        var scope = _canvas.CanvasTransform.ElementIdentity;
        UIHitRecord[] hits =
        [
            Region() with { FocusScopeIdentity = scope },
            Other() with { FocusScopeIdentity = scope, NodeIndex = 2, ParentIndex = 1, SubtreeEndIndex = 3 },
        ];
        UIPublication packet = new(++_generation, _canvas.PublicationLifetime, _generation,
            _generation, (long)_generation, default, new[] { scope }, hits, [], Bounds,
            Matrix4x4.Identity, Matrix4x4.Identity);
        try { _canvas.MarkPresented(_canvas, packet); }
        finally { packet.Release(); }
    }

    private void PublishWithMapping(Matrix4x4 outputToCanvas, params UIHitRecord[] hits)
    {
        Matrix4x4.Invert(outputToCanvas, out Matrix4x4 canvasToOutput);
        UIPublication packet = new(++_generation, _canvas.PublicationLifetime, _generation,
            _generation, (long)_generation, default, default, hits, [], Bounds, canvasToOutput, outputToCanvas);
        try { _canvas.MarkPresented(_canvas, packet); }
        finally { packet.Release(); }
    }
}
