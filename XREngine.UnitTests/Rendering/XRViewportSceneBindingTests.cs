using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class XRViewportSceneBindingTests
{
    private UITestShaderScope _shaders = null!;
    private RuntimeWorldRenderer _world = null!;
    private SceneNode _root = null!;
    private CameraComponent _camera = null!;
    private CameraComponent _replacement = null!;
    private XRViewport _target = null!;

    [SetUp]
    public void SetUp()
    {
        _shaders = new UITestShaderScope();
        TestWorldContext context = new();
        _world = new RuntimeWorldRenderer(context, new VisualScene3D());
        _root = new SceneNode("Viewport cameras");
        _root.SetWorldContext(context);
        SceneNode first = new(_root, "First camera");
        SceneNode second = new(_root, "Replacement camera");
        _camera = first.AddComponent<CameraComponent>()!;
        _replacement = second.AddComponent<CameraComponent>()!;
        _target = new XRViewport(null) { SetRenderPipelineFromCamera = false, CameraComponent = _camera, WorldInstanceOverride = _world };
    }

    [TearDown]
    public void TearDown()
    {
        _target?.Destroy();
        _root?.Destroy(true);
        _world?.Dispose();
        _shaders?.Dispose();
    }

    [Test]
    public void DetachmentCannotBeBlockedByAPropertyChangingHandler()
    {
        _target.PropertyChanging += (_, _) => throw new InvalidOperationException("A vetoable setter was used.");
        Assert.DoesNotThrow(_target.DetachSceneBinding);
        AssertDetached();
        Assert.That(_camera.Camera.Viewports, Does.Not.Contain(_target));
    }

    [Test]
    public void DetachmentClearsAllOwnersBeforeAChangedCallbackThrows()
    {
        InvalidOperationException failure = new("Expected notification failure.");
        _target.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(XRViewport.CameraComponent))
                throw failure;
        };
        Assert.That(Assert.Throws<InvalidOperationException>(_target.DetachSceneBinding), Is.SameAs(failure));
        AssertDetached();
        Assert.That(_camera.Camera.Viewports, Does.Not.Contain(_target));
    }

    [Test]
    public void DetachmentDuringPendingChangeCancelsTheOuterAssignment()
    {
        _target.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(XRViewport.CameraComponent))
                _target.DetachSceneBinding();
        };
        _target.CameraComponent = _replacement;
        AssertDetached();
        Assert.That(_replacement.Camera.Viewports, Does.Not.Contain(_target));
    }

    [Test]
    public void DetachmentNotificationCannotInstallAnotherOwner()
    {
        _target.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(XRViewport.CameraComponent))
                return;
            _target.CameraComponent = _replacement;
            _target.Camera = _replacement.Camera;
            _target.WorldInstanceOverride = _world;
        };
        _target.DetachSceneBinding();
        AssertDetached();
        Assert.That(_replacement.Camera.Viewports, Does.Not.Contain(_target));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DetachmentBypassesCameraListRemovalVetoes(bool throwFromVeto)
    {
        int vetoCalls = 0;
        int cameraRemovalCalls = 0;
        bool RejectRemoval()
        {
            vetoCalls++;
            if (throwFromVeto)
                throw new InvalidOperationException("A vetoable list removal was used.");
            return false;
        }
        _camera.Camera.Viewports.PreModified += RejectRemoval;
        _camera.Camera.Viewports.PreRemoved += _ => RejectRemoval();
        _camera.Camera.Viewports.PreAnythingRemoved += _ => RejectRemoval();
        _camera.Camera.ViewportRemoved += (_, viewport) =>
        {
            Assert.That(viewport, Is.SameAs(_target));
            Assert.That(_camera.Camera.Viewports, Does.Not.Contain(_target));
            cameraRemovalCalls++;
        };
        Assert.That(HasCameraResizeSubscription(), Is.True);

        Assert.DoesNotThrow(_target.DetachSceneBinding);

        AssertDetached();
        Assert.That(vetoCalls, Is.Zero);
        Assert.That(cameraRemovalCalls, Is.EqualTo(1));
        Assert.That(_camera.Camera.Viewports, Does.Not.Contain(_target));
        Assert.That(HasCameraResizeSubscription(), Is.False);
    }

    [Test]
    public void DetachmentRunsAllRemovalObserversAfterAnEarlierObserverThrows()
    {
        InvalidOperationException firstFailure = new("Expected first removal failure.");
        int removedCalls = 0;
        int cameraRemovalCalls = 0;
        int anythingRemovedCalls = 0;
        int modifiedCalls = 0;
        int changedCalls = 0;
        _camera.Camera.Viewports.PostRemoved += _ =>
        {
            AssertDetached();
            Assert.That(_camera.Camera.Viewports, Does.Not.Contain(_target));
            throw firstFailure;
        };
        _camera.Camera.Viewports.PostRemoved += _ => removedCalls++;
        _camera.Camera.ViewportRemoved += (_, _) => cameraRemovalCalls++;
        _camera.Camera.Viewports.PostAnythingRemoved += _ => throw new InvalidOperationException("Later removal failure.");
        _camera.Camera.Viewports.PostAnythingRemoved += _ => anythingRemovedCalls++;
        _camera.Camera.Viewports.PostModified += () => throw new InvalidOperationException("Modification failure.");
        _camera.Camera.Viewports.PostModified += () => modifiedCalls++;
        _camera.Camera.Viewports.CollectionChanged += (_, _) => throw new InvalidOperationException("Collection failure.");
        _camera.Camera.Viewports.CollectionChanged += (_, _) => changedCalls++;
        Assert.That(HasCameraResizeSubscription(), Is.True);

        Assert.That(Assert.Throws<InvalidOperationException>(_target.DetachSceneBinding), Is.SameAs(firstFailure));

        AssertDetached();
        Assert.That(_camera.Camera.Viewports, Does.Not.Contain(_target));
        Assert.That(HasCameraResizeSubscription(), Is.False);
        Assert.That(removedCalls, Is.EqualTo(1));
        Assert.That(cameraRemovalCalls, Is.EqualTo(1));
        Assert.That(anythingRemovedCalls, Is.EqualTo(1));
        Assert.That(modifiedCalls, Is.EqualTo(1));
        Assert.That(changedCalls, Is.EqualTo(1));
    }

    private bool HasCameraResizeSubscription()
    {
        var handlers = (Action<XRViewport>?)typeof(XRViewport)
            .GetField(nameof(XRViewport.Resized), BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_target);
        return handlers?.GetInvocationList().Any(handler => ReferenceEquals(handler.Target, _camera)) == true;
    }

    private void AssertDetached()
    {
        Assert.That(_target.CameraComponent, Is.Null);
        Assert.That(_target.Camera, Is.Null);
        Assert.That(_target.ActiveCamera, Is.Null);
        Assert.That(_target.WorldInstanceOverride, Is.Null);
        Assert.That(_target.World, Is.Null);
        Assert.That(_target.Window, Is.Null);
    }
}
