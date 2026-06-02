using NUnit.Framework;
using XREngine.Editor;
using XREngine.Rendering;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Components.Editing;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public class EditorViewCameraToolingTests
{
    [Test]
    public void ConfigureEditorViewCamera_SuppressesTransformToolAndTransformDebugMarkers()
    {
        TransformTool3D.DestroyInstance();

        try
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
        finally
        {
            TransformTool3D.DestroyInstance();
        }
    }
}
