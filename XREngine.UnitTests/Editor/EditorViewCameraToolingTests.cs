using NUnit.Framework;
using System.Numerics;
using System.Reflection;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Editor;
using XREngine.Rendering;
using XREngine.Scene.Physics.Jitter2;
using XREngine.Scene.Physics.Physx;
using XREngine.UnitTests.Rendering;
using XREngine.Scene;
using XREngine.Scene.Components.Editing;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public class EditorViewCameraToolingTests
{
    private IRuntimeShaderServices? _previousShaderServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
        TransformTool3D.DestroyInstance();
    }

    [TearDown]
    public void TearDown()
    {
        Selection.SceneNodes = [];
        TransformTool3D.DestroyInstance();
        RuntimeShaderServices.Current = _previousShaderServices;
    }

    [Test]
    public void ConfigureEditorViewCamera_SuppressesTransformToolAndTransformDebugMarkers()
    {
        using RuntimeWorld world = new(new JitterScene());
        using RuntimeWorldRenderer renderWorld = new(world, new VisualScene3D());
        SceneNode root = new("Root");
        SceneNode cameraNode = new(root, "Editor View");

        world.RootNodes.Add(root);
        EditorUnitTests.Pawns.ConfigureEditorViewCamera(root, cameraNode);

        Assert.Multiple(() =>
        {
            Assert.That(cameraNode.IsEditorOnly, Is.True);
            Assert.That(cameraNode.CanDeactivate, Is.False);
            Assert.That(cameraNode.SuppressTransformDebugLineAndPoint, Is.True);
            Assert.That(cameraNode.SuppressTransformTools, Is.True);
            Assert.That(TransformTool3D.GetInstance(cameraNode.Transform), Is.Null);
            Assert.That(TransformTool3D.InstanceNode, Is.Null);
        });
    }

    [Test]
    public void TransformTool3D_ConstructsForSelectableNode()
    {
        using RuntimeWorld world = new(new JitterScene());
        using RuntimeWorldRenderer renderWorld = new(world, new VisualScene3D());
        SceneNode root = new("Root");
        SceneNode target = new(root, "Target");

        world.RootNodes.Add(root);

        TransformTool3D? tool = null;
        Assert.DoesNotThrow(() => tool = TransformTool3D.GetInstance(target.Transform));

        Assert.Multiple(() =>
        {
            Assert.That(tool, Is.Not.Null);
            Assert.That(TransformTool3D.InstanceNode, Is.Not.Null);
            Assert.That(TransformTool3D.InstanceNode?.World, Is.SameAs(world));
        });
    }

    [Test]
    public void TransformTool3D_ForeignCancelCannotReleaseDrag_AndReentryKeepsNewOwner()
    {
        using RuntimeWorld world = new(new JitterScene());
        using RuntimeWorldRenderer renderWorld = new(world, new VisualScene3D());
        SceneNode root = new("Root");
        SceneNode target = new(root, "Target");
        SceneNode cameraNode = new(root, "Camera");
        world.RootNodes.Add(root);
        CameraComponent camera = cameraNode.AddComponent<CameraComponent>()!;
        var viewport = new XRViewport(null) { CameraComponent = camera };
        TransformTool3D tool = TransformTool3D.GetInstance(target.Transform)
            ?? throw new AssertionException("The transform tool was not created.");
        object firstOwner = new();
        object nextOwner = new();
        Segment ray = new(Vector3.UnitZ, -Vector3.UnitZ);

        tool.MouseMove(ray, camera.Camera, viewport, false, firstOwner);
        Assert.That(ReadPointerOwner(tool), Is.SameAs(firstOwner));
        SetToolPressed(tool);
        tool.CancelPointerInteraction(nextOwner);
        Assert.That(ReadPointerOwner(tool), Is.SameAs(firstOwner));

        int releases = 0;
        tool.MouseUp += () =>
        {
            releases++;
            tool.MouseMove(ray, camera.Camera, viewport, false, nextOwner);
        };
        tool.CancelPointerInteraction(firstOwner);

        Assert.Multiple(() =>
        {
            Assert.That(releases, Is.EqualTo(1));
            Assert.That(ReadPointerOwner(tool), Is.SameAs(nextOwner));
        });
    }

    [Test]
    public void TransformTool3D_DirectDestroyReleasesOwnedDragOnce()
    {
        using RuntimeWorld world = new(new JitterScene());
        using RuntimeWorldRenderer renderWorld = new(world, new VisualScene3D());
        SceneNode root = new("Root");
        SceneNode target = new(root, "Target");
        SceneNode cameraNode = new(root, "Camera");
        world.RootNodes.Add(root);
        CameraComponent camera = cameraNode.AddComponent<CameraComponent>()!;
        var viewport = new XRViewport(null) { CameraComponent = camera };
        TransformTool3D tool = TransformTool3D.GetInstance(target.Transform)
            ?? throw new AssertionException("The transform tool was not created.");
        tool.MouseMove(new Segment(Vector3.UnitZ, -Vector3.UnitZ), camera.Camera, viewport, false, new object());
        SetToolPressed(tool);
        int releases = 0;
        tool.MouseUp += () => releases++;

        tool.Destroy(true);

        Assert.Multiple(() =>
        {
            Assert.That(releases, Is.EqualTo(1));
            Assert.That(ReadPointerOwner(tool), Is.Null);
            Assert.That(tool.IsDestroyed, Is.True);
        });
    }

    [Test]
    public void EditorPawn_DirectDestroyStopsChangingGlobalTransformTarget()
    {
        using RuntimeWorld world = new(new JitterScene());
        using RuntimeWorldRenderer renderWorld = new(world, new VisualScene3D());
        SceneNode root = new("Root");
        SceneNode cameraNode = new(root, "Camera");
        SceneNode first = new(root, "First");
        SceneNode second = new(root, "Second");
        world.RootNodes.Add(root);
        CameraComponent camera = cameraNode.AddComponent<CameraComponent>()!;
        EditorFlyingCameraPawnComponent pawn = cameraNode.AddComponent<EditorFlyingCameraPawnComponent>()!;
        pawn.CameraComponent = camera;
        if (cameraNode.IsActiveSelf)
            cameraNode.OnActivated();
        try
        {
            Selection.SceneNodes = [first];
            Assert.That(TransformTool3D.GetActiveInstance(out TransformTool3D? tool), Is.True);
            Assert.That(tool?.TargetSocket, Is.SameAs(first.Transform));

            pawn.Destroy(true);
            Selection.SceneNodes = [second];

            Assert.That(tool?.TargetSocket, Is.SameAs(first.Transform));
        }
        finally
        {
            pawn.Destroy(true);
            Selection.SceneNodes = [];
        }
    }

    private static object? ReadPointerOwner(TransformTool3D tool)
        => typeof(TransformTool3D).GetField("_pointerOwner", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(tool);

    private static void SetToolPressed(TransformTool3D tool)
        => typeof(TransformTool3D).GetField("_pressed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tool, true);
}
