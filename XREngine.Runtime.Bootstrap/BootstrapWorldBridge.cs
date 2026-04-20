using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

public interface IBootstrapWorldBridge
{
    XRWorld? CreateSpecializedWorld(UnitTestWorldKind worldKind, bool setUI, bool isServer);
}

public static class BootstrapWorldBridge
{
    public static IBootstrapWorldBridge? Current { get; set; }
}