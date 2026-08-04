using XREngine.Components.Scene;

namespace XREngine.Runtime.Bootstrap;

public static class BootstrapEditorBridge
{
    public static IBootstrapEditorBridge? Current { get; set; }
}
