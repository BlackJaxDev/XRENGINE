using System;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

public interface IBootstrapModelImportBridge
{
    void ImportModels(string desktopDir, SceneNode rootNode, SceneNode characterParentNode, Action? onAllImportsComplete = null);
}

public static class BootstrapModelImportBridge
{
    public static IBootstrapModelImportBridge? Current { get; set; }
}
