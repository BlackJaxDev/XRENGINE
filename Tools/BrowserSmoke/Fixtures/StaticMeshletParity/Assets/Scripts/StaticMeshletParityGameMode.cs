using XREngine;
using XREngine.Components;
using XREngine.Input;

namespace StaticMeshletParity;

/// <summary>Possesses the saved fixed inspection camera.</summary>
public sealed class StaticMeshletParityGameMode : GameMode
{
    private string _fixtureMarker = string.Empty;

    public string FixtureMarker { get => _fixtureMarker; set => SetField(ref _fixtureMarker, value); }

    public override XRComponent? CreateDefaultPawn(ELocalPlayerIndex playerIndex) => null;

    public override void OnBeginPlay()
    {
        base.OnBeginPlay();
        if (WorldInstance is not RuntimeWorld world)
            throw new InvalidOperationException("The static meshlet game mode requires a runtime world.");
        for (int index = 0; index < world.RootNodes.Count; index++)
        {
            StaticMeshletParityPawnComponent? pawn = world.RootNodes[index]
                .FindFirstDescendantComponent<StaticMeshletParityPawnComponent>();
            if (pawn is null)
                continue;
            pawn.PossessByLocalPlayer(ELocalPlayerIndex.One);
            return;
        }
        throw new InvalidOperationException("The static meshlet world has no authored inspection pawn.");
    }
}
