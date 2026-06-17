using XREngine.Rendering;

namespace XREngine;

internal sealed class RuntimeEngineState
{
    public IPawnController GetOrCreateLocalPlayer(ELocalPlayerIndex playerIndex)
        => GetLocalPlayer(playerIndex) ?? new NullPawnController(playerIndex);

    public IPawnController? GetLocalPlayer(ELocalPlayerIndex playerIndex)
        => RuntimeRenderingHostServices.Current.EnumerateLocalPlayers().FirstOrDefault(player => player.LocalPlayerIndex == playerIndex);
}
