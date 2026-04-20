using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

public interface IBootstrapModelImportBridge
{
    void ImportModels(string desktopDir, SceneNode rootNode, SceneNode characterParentNode);
}

public static class BootstrapModelImportBridge
{
    public static IBootstrapModelImportBridge? Current { get; set; }
}