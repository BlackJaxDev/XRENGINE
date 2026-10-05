using XREngine;
using XREngine.Components;
using XREngine.Input;
using XREngine.Input.Devices;

namespace ModularPipelineParity;

/// <summary>Connects one saved camera to the local player's ordinary viewport.</summary>
public sealed class ModularPipelineParityPawnComponent : PawnComponent
{
    private string _profileKey = string.Empty;

    public string ProfileKey
    {
        get => _profileKey;
        set => SetField(ref _profileKey, value);
    }

    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        CameraComponent = SceneNode.GetComponent<CameraComponent>()
            ?? throw new InvalidOperationException("A modular pipeline pawn requires its saved camera.");
    }

    public override void RegisterInput(object inputInterface)
    {
        if (inputInterface is not InputInterface input)
            return;
        input.RegisterKeyEvent(EKey.Number1, EButtonInputType.Pressed, SelectClearA);
        input.RegisterKeyEvent(EKey.Number2, EButtonInputType.Pressed, SelectClearB);
        input.RegisterKeyEvent(EKey.Number3, EButtonInputType.Pressed, SelectQuad);
        input.RegisterKeyEvent(EKey.Number4, EButtonInputType.Pressed, SelectMsaaCpu);
        input.RegisterKeyEvent(EKey.Number5, EButtonInputType.Pressed, SelectMsaaGpu);
    }

    private void SelectClearA() => SelectProfile(0);
    private void SelectClearB() => SelectProfile(1);
    private void SelectQuad() => SelectProfile(2);
    private void SelectMsaaCpu() => SelectProfile(3);
    private void SelectMsaaGpu() => SelectProfile(4);

    private void SelectProfile(int index)
    {
        if (WorldAs<RuntimeWorld>()?.GameMode is ModularPipelineParityGameMode mode)
            mode.SelectProfile(index);
    }
}
