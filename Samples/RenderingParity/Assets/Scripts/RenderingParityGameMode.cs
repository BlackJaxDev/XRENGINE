using XREngine;
using XREngine.Components;
using XREngine.Input;

namespace RenderingParity;

/// <summary>Uses the serialized inspection pawn rather than constructing a replacement player rig.</summary>
public sealed class RenderingParityGameMode : GameMode
{
    private string _fixtureMarker = string.Empty;

    /// <summary>Identifies the saved scene contract after source loading and cooked hydration.</summary>
    public string FixtureMarker { get => _fixtureMarker; set => SetField(ref _fixtureMarker, value); }

    public override XRComponent? CreateDefaultPawn(ELocalPlayerIndex playerIndex) => null;

    public override void OnBeginPlay()
    {
        base.OnBeginPlay();
        if (WorldInstance is not RuntimeWorld world)
            throw new InvalidOperationException("The rendering game mode requires the engine runtime world.");
        for (int index = 0; index < world.RootNodes.Count; index++)
        {
            RenderingParityPawnComponent? pawn = world.RootNodes[index].FindFirstDescendantComponent<RenderingParityPawnComponent>();
            if (pawn is null)
                continue;
            pawn.PossessByLocalPlayer(ELocalPlayerIndex.One);
            return;
        }
        throw new InvalidOperationException("The rendering world has no authored inspection pawn.");
    }
}
