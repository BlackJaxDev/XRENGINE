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

public interface IBootstrapWorldBridge
{
    XRWorld? CreateSpecializedWorld(UnitTestWorldKind worldKind, bool setUI, bool isServer);
}

public static class BootstrapWorldBridge
{
    public static IBootstrapWorldBridge? Current { get; set; }
}

public interface IBootstrapModelImportBridge
{
    void ImportModels(string desktopDir, SceneNode rootNode, SceneNode characterParentNode);
}

public static class BootstrapModelImportBridge
{
    public static IBootstrapModelImportBridge? Current { get; set; }
}