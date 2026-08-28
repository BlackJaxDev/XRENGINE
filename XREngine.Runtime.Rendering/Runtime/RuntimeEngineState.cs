using XREngine.Rendering;
using XREngine.Input;

namespace XREngine;

internal sealed class RuntimeEngineState
{
    public IPawnController? MainPlayer => RuntimePlayerControllerServices.Current?.GetLocalPlayer(ELocalPlayerIndex.One);

    public IPawnController GetOrCreateLocalPlayer(ELocalPlayerIndex playerIndex)
        => RuntimePlayerControllerServices.Current?.GetOrCreateLocalPlayer(playerIndex)
            ?? throw new InvalidOperationException("No local-player registry is installed for this runtime profile.");

    public IPawnController? GetLocalPlayer(ELocalPlayerIndex playerIndex)
        => RuntimePlayerControllerServices.Current?.GetLocalPlayer(playerIndex);
}
