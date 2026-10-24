using NUnit.Framework;
using XREngine.Components;
using XREngine.Editor;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Physics.Jitter2;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeSceneCameraOwnershipTests
{
    [Test]
    public void CameraClearContinuesAfterComponentNotificationThrows()
    {
        using UITestShaderScope shaders = new();
        using RuntimeWorld world = new(new JitterScene());
        using RuntimeWorldRenderer renderWorld = new(world, new VisualScene3D());
        SceneNode node = new("Camera");
        world.RootNodes.Add(node);
        CameraComponent camera = node.AddComponent<CameraComponent>()!;
        XRViewport target = new(null) { SetRenderPipelineFromCamera = false, CameraComponent = camera, WorldInstanceOverride = renderWorld };
        InvalidOperationException failure = new("Expected camera callback failure.");
        target.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(XRViewport.CameraComponent) && target.CameraComponent is null)
                throw failure;
        };
        try
        {
            Assert.That(Assert.Throws<InvalidOperationException>(() => SceneViewPanel.ClearViewportCamera(target)), Is.SameAs(failure));
            Assert.That(target.CameraComponent, Is.Null);
            Assert.That(target.ActiveCamera, Is.Null);
            Assert.That(target.WorldInstanceOverride, Is.Null);
            Assert.That(target.Window, Is.Null);
        }
        finally { target.Destroy(); }
    }

    [Test]
    public void CameraBindingRejectsAReplacementFromItsNotification()
    {
        using UITestShaderScope shaders = new();
        using RuntimeWorld world = new(new JitterScene());
        using RuntimeWorldRenderer renderWorld = new(world, new VisualScene3D());
        SceneNode firstNode = new("First camera"), replacementNode = new("Replacement camera");
        world.RootNodes.Add(firstNode);
        world.RootNodes.Add(replacementNode);
        CameraComponent first = firstNode.AddComponent<CameraComponent>()!;
        CameraComponent replacement = replacementNode.AddComponent<CameraComponent>()!;
        XRViewport target = new(null) { SetRenderPipelineFromCamera = false };
        target.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(XRViewport.CameraComponent) && ReferenceEquals(target.CameraComponent, first))
                target.CameraComponent = replacement;
        };
        try
        {
            Assert.That(SceneViewPanel.TryBindOwnedCamera(target, null, renderWorld, first), Is.False);
            Assert.That(target.CameraComponent, Is.Null);
            Assert.That(target.ActiveCamera, Is.Null);
            Assert.That(target.WorldInstanceOverride, Is.Null);
        }
        finally { target.Destroy(); }
    }

    [Test]
    public void CameraSelectionRejectsForeignWorldAndMissingOwner()
    {
        using UITestShaderScope shaders = new();
        using RuntimeWorld first = new(new JitterScene());
        using RuntimeWorld second = new(new JitterScene());
        using RuntimeWorldRenderer firstRenderer = new(first, new VisualScene3D());
        using RuntimeWorldRenderer secondRenderer = new(second, new VisualScene3D());
        SceneNode localNode = new("Local camera");
        SceneNode foreignNode = new("Foreign camera");
        first.RootNodes.Add(localNode);
        second.RootNodes.Add(foreignNode);
        CameraComponent local = localNode.AddComponent<CameraComponent>()!;
        CameraComponent foreign = foreignNode.AddComponent<CameraComponent>()!;
        XRViewport localView = new(null) { SetRenderPipelineFromCamera = false, CameraComponent = local, WorldInstanceOverride = firstRenderer };
        XRViewport foreignView = new(null) { SetRenderPipelineFromCamera = false, CameraComponent = foreign, WorldInstanceOverride = secondRenderer };
        try
        {
            XRViewport[] sources = [foreignView, localView];
            Assert.That(SceneViewPanel.ResolveOwnedCamera(null, firstRenderer, sources, foreign), Is.SameAs(local));
            Assert.That(SceneViewPanel.ResolveOwnedCamera(null, firstRenderer, sources, local), Is.SameAs(local));
            Assert.That(SceneViewPanel.ResolveOwnedCamera(null, null, sources, local), Is.Null);
            foreignView.WorldInstanceOverride = firstRenderer;
            Assert.That(SceneViewPanel.ResolveOwnedCamera(null, firstRenderer, [foreignView], foreign), Is.Null,
                "A viewport override cannot make its foreign camera belong to this world.");
            local.Destroy(true);
            Assert.That(SceneViewPanel.ResolveOwnedCamera(null, firstRenderer, sources, local), Is.Null);
        }
        finally
        {
            localView.Destroy();
            foreignView.Destroy();
        }
    }
}
