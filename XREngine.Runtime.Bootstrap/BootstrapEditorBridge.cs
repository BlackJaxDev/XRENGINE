using XREngine.Components;
using XREngine.Components.Scene;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

public interface IBootstrapEditorBridge
{
    void CreateEditorUi(SceneNode parent, CameraComponent? camera, PawnComponent? pawn);
    void EnableTransformToolForNode(SceneNode node);
}

public static class BootstrapEditorBridge
{
    public static IBootstrapEditorBridge? Current { get; set; }
}