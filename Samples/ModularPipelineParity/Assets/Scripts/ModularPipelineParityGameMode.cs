using XREngine;
using XREngine.Components;
using XREngine.Input;

namespace ModularPipelineParity;

/// <summary>Selects saved cameras through the ordinary possessed pawn's input bindings.</summary>
public sealed class ModularPipelineParityGameMode : GameMode
{
    private string _fixtureMarker = string.Empty;
    private ModularPipelineParityPawnComponent[]? _pawns;
    private int _activeProfile = -1;

    public string FixtureMarker
    {
        get => _fixtureMarker;
        set => SetField(ref _fixtureMarker, value);
    }

    public override XRComponent? CreateDefaultPawn(ELocalPlayerIndex playerIndex) => null;

    public override void OnBeginPlay()
    {
        base.OnBeginPlay();
        if (WorldInstance is not RuntimeWorld world)
            throw new InvalidOperationException("The modular pipeline mode requires the runtime world.");
        _pawns = ModularPipelineParityWorldContract.Validate(world);
        _activeProfile = -1;
        SelectProfile(0);
    }

    public override void OnEndPlay()
    {
        _pawns = null;
        base.OnEndPlay();
    }

    public void SelectProfile(int index)
    {
        if (_pawns is null || index < 0 || index >= _pawns.Length || index == _activeProfile)
            return;
        ModularPipelineParityPawnComponent pawn = _pawns[index];
        pawn.PossessByLocalPlayer(ELocalPlayerIndex.One);
        if (!ReferenceEquals(RuntimePlayerControllerServices.Current?
            .GetOrCreateLocalPlayer(ELocalPlayerIndex.One).ControlledPawnComponent, pawn))
            throw new InvalidOperationException("The requested modular camera was not possessed by the local player.");
        _activeProfile = index;
        CameraComponent camera = pawn.SceneNode.GetComponent<CameraComponent>()
            ?? throw new InvalidOperationException("The selected modular pawn lost its authored camera.");
        Console.WriteLine($"ModularPipelineParity active authored camera: {pawn.ProfileKey} " +
            $"source={camera.RenderPipelineSource?.ID} aa={camera.AntiAliasingModeOverride}");
    }
}
