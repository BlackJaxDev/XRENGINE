using XREngine.Components;
using XREngine.Components.Scene;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

public interface IBootstrapEditorBridge
{
    void CreateEditorUi(SceneNode parent, CameraComponent? camera, PawnComponent? pawn);
    void EnableTransformToolForNode(SceneNode node);
    XRWorld? CreateSpecializedWorld(UnitTestWorldKind worldKind, bool setUI, bool isServer);
    void ImportModels(string desktopDir, SceneNode rootNode, SceneNode characterParentNode);
    PawnComponent? CreateFlyableCameraPawn(SceneNode cameraNode);
}

public static class BootstrapEditorBridge
{
    public static IBootstrapEditorBridge? Current { get; set; }
}