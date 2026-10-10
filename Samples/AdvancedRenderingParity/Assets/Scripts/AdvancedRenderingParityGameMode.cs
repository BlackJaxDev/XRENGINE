using XREngine;
using XREngine.Components;
using XREngine.Input;

namespace AdvancedRenderingParity;

/// <summary>Possesses the saved camera pawn without creating a replacement rig.</summary>
public sealed class AdvancedRenderingParityGameMode : GameMode
{
    private string _fixtureMarker = string.Empty;

    public string FixtureMarker { get => _fixtureMarker; set => SetField(ref _fixtureMarker, value); }

    public override XRComponent? CreateDefaultPawn(ELocalPlayerIndex playerIndex) => null;

    public override void OnBeginPlay()
    {
        base.OnBeginPlay();
        if (WorldInstance is not RuntimeWorld world)
            throw new InvalidOperationException("The rendering game mode requires the engine runtime world.");
        for (int index = 0; index < world.RootNodes.Count; index++)
        {
            AdvancedRenderingParityPawnComponent? pawn = world.RootNodes[index]
                .FindFirstDescendantComponent<AdvancedRenderingParityPawnComponent>();
            if (pawn is null)
                continue;
            pawn.PossessByLocalPlayer(ELocalPlayerIndex.One);
            return;
        }
        throw new InvalidOperationException("The rendering world has no authored inspection pawn.");
    }
}
