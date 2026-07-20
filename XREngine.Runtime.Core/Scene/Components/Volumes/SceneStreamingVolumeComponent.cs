using System.ComponentModel;
using System.Threading;

namespace XREngine.Components.Scene.Volumes;

/// <summary>
/// Streams a host-owned scene into and out of the current runtime world while this volume is occupied.
/// </summary>
[Description("Streams a scene into/out of the current world based on overlaps.")]
public class SceneStreamingVolumeComponent : TriggerVolumeComponent
{
    private string _sceneAssetPath = string.Empty;
    private bool _loadOnEnter = true;
    private bool _unloadOnLeave = true;
    private IRuntimeSceneStreamingHandle? _loadedScene;
    private bool _loadedInWorld;
    private int _loadToken;

    /// <summary>
    /// Host-resolved path to the scene asset to stream.
    /// </summary>
    public string SceneAssetPath
    {
        get => _sceneAssetPath;
        set => SetField(ref _sceneAssetPath, value);
    }

    public bool LoadOnEnter
    {
        get => _loadOnEnter;
        set => SetField(ref _loadOnEnter, value);
    }

    public bool UnloadOnLeave
    {
        get => _unloadOnLeave;
        set => SetField(ref _unloadOnLeave, value);
    }

    protected override void OnEntered(XRComponent component)
    {
        base.OnEntered(component);

        if (!LoadOnEnter || OverlappingComponents.Count != 1)
            return;

        int token = Interlocked.Increment(ref _loadToken);
        _ = LoadAndAttachAsync(token);
    }

    protected override void OnLeft(XRComponent component)
    {
        base.OnLeft(component);

        if (!UnloadOnLeave || OverlappingComponents.Count != 0)
            return;

        int token = Interlocked.Increment(ref _loadToken);
        IRuntimeWorldContext? expectedWorld = World;
        DispatchToAppThread(
            () => DetachIfLoaded(token, expectedWorld),
            "SceneStreamingVolumeComponent.DetachOnLeave");
    }

    protected override void OnComponentDeactivated()
    {
        int token = Interlocked.Increment(ref _loadToken);
        IRuntimeWorldContext? expectedWorld = World;
        DispatchToAppThread(
            () => DetachIfLoaded(token, expectedWorld),
            "SceneStreamingVolumeComponent.DetachOnDeactivate");
        base.OnComponentDeactivated();
    }

    private async Task LoadAndAttachAsync(int token)
    {
        IRuntimeWorldContext? expectedWorld = World;
        if (expectedWorld is null || string.IsNullOrWhiteSpace(SceneAssetPath))
            return;

        IRuntimeSceneStreamingHandle? scene = await RuntimeSceneStreamingHostServices.Current
            .LoadSceneAsync(SceneAssetPath)
            .ConfigureAwait(false);
        if (scene is null)
            return;

        DispatchToAppThread(
            () => AttachIfEligible(token, expectedWorld, scene),
            "SceneStreamingVolumeComponent.AttachLoadedScene");
    }

    private void AttachIfEligible(
        int token,
        IRuntimeWorldContext expectedWorld,
        IRuntimeSceneStreamingHandle scene)
    {
        if (token != _loadToken
            || !ReferenceEquals(World, expectedWorld)
            || OverlappingComponents.Count == 0
            || _loadedInWorld)
            return;

        if (!RuntimeSceneStreamingHostServices.Current.AttachScene(expectedWorld, scene))
            return;

        _loadedScene = scene;
        _loadedInWorld = true;
    }

    private void DetachIfLoaded(int token, IRuntimeWorldContext? expectedWorld)
    {
        if (token != _loadToken
            || !_loadedInWorld
            || expectedWorld is null
            || !ReferenceEquals(World, expectedWorld)
            || _loadedScene is not { } scene)
            return;

        if (!RuntimeSceneStreamingHostServices.Current.DetachScene(expectedWorld, scene))
            return;

        _loadedInWorld = false;
        _loadedScene = null;
    }

    private static void DispatchToAppThread(Action action, string reason)
        => RuntimeThreadServices.Current.InvokeOnAppThread(
            action,
            reason,
            executeNowIfAlreadyAppThread: true);
}
