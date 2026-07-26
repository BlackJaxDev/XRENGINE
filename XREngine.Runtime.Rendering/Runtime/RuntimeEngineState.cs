using XREngine.Rendering;

namespace XREngine;

internal sealed class RuntimeEngineState
{
    public IPawnController? MainPlayer => GetLocalPlayer(ELocalPlayerIndex.One);

    public IPawnController GetOrCreateLocalPlayer(ELocalPlayerIndex playerIndex)
        => GetLocalPlayer(playerIndex) ?? new NullPawnController(playerIndex);

    public IPawnController? GetLocalPlayer(ELocalPlayerIndex playerIndex)
        => RuntimeRenderingHostServices.Factories.EnumerateLocalPlayers().FirstOrDefault(player => player.LocalPlayerIndex == playerIndex);
}
