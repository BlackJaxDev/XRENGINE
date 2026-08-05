using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

public interface IBootstrapWorldBridge
{
    XRWorld? CreateSpecializedWorld(UnitTestWorldKind worldKind, bool setUI, bool isServer);
}
