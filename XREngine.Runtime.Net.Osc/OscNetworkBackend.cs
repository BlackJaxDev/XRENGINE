using XREngine.Core.Files;
using XREngine.Components;
using XREngine.Data.Components;

namespace XREngine.Networking;

/// <summary>Installs OSC and motion capture components without opening sockets during composition.</summary>
public static class OscNetworkBackend
{
    public static void Register()
    {
        CookedBinarySerializer.RegisterRuntimeFactory(static () => new OscSenderComponent());
        CookedBinarySerializer.RegisterRuntimeFactory(static () => new OscReceiverComponent());
        CookedBinarySerializer.RegisterRuntimeFactory(static () => new FaceTrackingReceiverComponent());
        CookedBinarySerializer.RegisterRuntimeFactory(static () => new VMCCaptureComponent());
        CookedBinarySerializer.RegisterRuntimeFactory(static () => new VMCSenderComponent());
    }
}
