using XREngine;
using XREngine.Components;
using XREngine.Input;

namespace ModularPipelineParity;

/// <summary>Selects saved cameras through the ordinary possessed pawn's input bindings.</summary>
public sealed class ModularPipelineParityGameMode : GameMode
{
    private string _fixtureMarker = string.Empty;
    private ModularPipelineParityPawnComponent[]? _pawns;
    private CustomMsaaSceneComponent[]? _sceneParts;
    private ModularMsaaRenderPipeline? _msaaCpuSource;
    private ModularMsaaRenderPipeline? _msaaGpuSource;
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
        _sceneParts = ModularPipelineParityWorldContract.ValidateSceneParts(world);
        _msaaCpuSource = RequireMsaaSource(_pawns[3]);
        _msaaGpuSource = RequireMsaaSource(_pawns[4]);
        _msaaCpuSource.WebProgramsBound += ConfigureMsaaScene;
        _msaaGpuSource.WebProgramsBound += ConfigureMsaaScene;
        if (_msaaCpuSource.WebPipelineArtifacts is not null)
            ConfigureMsaaScene(_msaaCpuSource);
        else if (_msaaGpuSource.WebPipelineArtifacts is not null)
            ConfigureMsaaScene(_msaaGpuSource);
        _activeProfile = -1;
        SelectProfile(0);
    }

    public override void OnEndPlay()
    {
        if (_msaaCpuSource is not null)
            _msaaCpuSource.WebProgramsBound -= ConfigureMsaaScene;
        if (_msaaGpuSource is not null)
            _msaaGpuSource.WebProgramsBound -= ConfigureMsaaScene;
        _msaaCpuSource = null;
        _msaaGpuSource = null;
        _sceneParts = null;
        _pawns = null;
        base.OnEndPlay();
    }

    private static ModularMsaaRenderPipeline RequireMsaaSource(ModularPipelineParityPawnComponent pawn)
        => pawn.SceneNode.GetComponent<CameraComponent>()?.RenderPipelineSource as ModularMsaaRenderPipeline
            ?? throw new InvalidOperationException($"The '{pawn.ProfileKey}' camera lost its authored MSAA source.");

    private void ConfigureMsaaScene(ModularMsaaRenderPipeline source)
    {
        if (_sceneParts is null)
            return;
        foreach (CustomMsaaSceneComponent part in _sceneParts)
            part.Configure(source);
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
        string msaaDetails = camera.RenderPipelineSource is ModularMsaaRenderPipeline msaa
            ? $" samples={camera.MsaaSampleCountOverride} strategy={msaa.MeshSubmissionStrategy}"
            : string.Empty;
        Console.WriteLine($"ModularPipelineParity active authored camera: {pawn.ProfileKey} " +
            $"source={camera.RenderPipelineSource?.ID} aa={camera.AntiAliasingModeOverride}{msaaDetails}");
    }
}
