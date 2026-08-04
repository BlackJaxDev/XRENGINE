using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

public interface IBootstrapEditorBridge
{
    void CreateEditorUi(SceneNode parent, CameraComponent? camera, PawnComponent? pawn);
    void CreateCameraPreviewUi(CameraComponent camera, string label);
    void EnableTransformToolForNode(SceneNode node);
    void ConfigureEditorViewCamera(SceneNode parent, SceneNode cameraNode);
}
