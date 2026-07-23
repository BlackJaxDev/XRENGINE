using NUnit.Framework;
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
        TransformTool3D.DestroyInstance();
        RuntimeShaderServices.Current = _previousShaderServices;
    }

    [Test]
    public void ConfigureEditorViewCamera_SuppressesTransformToolAndTransformDebugMarkers()
    {
        XRWorldInstance world = new(new VisualScene3D(), new JitterScene());
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
        XRWorldInstance world = new(new VisualScene3D(), new JitterScene());
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
}
