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
    private IRuntimeSceneStreamingHostServices? _loadedHost;
    private IRuntimeWorldContext? _loadedWorld;
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

        if (!LoadOnEnter || OverlappingComponents.Count != 1 || IsDestroyed || IsDestroyQueued)
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
        DispatchToAppThread(
            () => DetachIfLoaded(token),
            "SceneStreamingVolumeComponent.DetachOnLeave");
    }

    protected override void OnComponentDeactivated()
    {
        int token = Interlocked.Increment(ref _loadToken);
        DispatchToAppThread(
            () => DetachIfLoaded(token),
            "SceneStreamingVolumeComponent.DetachOnDeactivate");
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        Interlocked.Increment(ref _loadToken);
        OverlappingComponents.Clear();
        IRuntimeSceneStreamingHostServices? host = _loadedHost;
        IRuntimeWorldContext? world = _loadedWorld;
        IRuntimeSceneStreamingHandle? scene = _loadedScene;
        _loadedHost = null;
        _loadedWorld = null;
        _loadedScene = null;
        _loadedInWorld = false;
        try
        {
            if (host is not null && world is not null && scene is not null)
                DispatchToAppThread(() => host.DetachScene(world, scene),
                    "SceneStreamingVolumeComponent.DetachOnDestroy");
        }
        finally
        {
            base.OnDestroying();
        }
    }

    private async Task LoadAndAttachAsync(int token)
    {
        IRuntimeWorldContext? expectedWorld = World;
        IRuntimeSceneStreamingHostServices host = RuntimeSceneStreamingHostServices.Current;
        string path = SceneAssetPath;
        if (expectedWorld is null || string.IsNullOrWhiteSpace(path) || IsDestroyed || IsDestroyQueued)
            return;

        IRuntimeSceneStreamingHandle? scene = await host
            .LoadSceneAsync(path)
            .ConfigureAwait(false);
        if (scene is null)
            return;

        DispatchToAppThread(
            () => AttachIfEligible(token, expectedWorld, host, scene),
            "SceneStreamingVolumeComponent.AttachLoadedScene");
    }

    private void AttachIfEligible(
        int token,
        IRuntimeWorldContext expectedWorld,
        IRuntimeSceneStreamingHostServices host,
        IRuntimeSceneStreamingHandle scene)
    {
        if (token != Volatile.Read(ref _loadToken)
            || IsDestroyed || IsDestroyQueued
            || SceneNode is { IsDestroyed: true } or { IsDestroyQueued: true }
            || !ReferenceEquals(World, expectedWorld)
            || OverlappingComponents.Count == 0
            || _loadedInWorld)
            return;

        if (!host.AttachScene(expectedWorld, scene))
            return;

        if (token != Volatile.Read(ref _loadToken) || IsDestroyed || IsDestroyQueued ||
            SceneNode is { IsDestroyed: true } or { IsDestroyQueued: true } ||
            !ReferenceEquals(World, expectedWorld))
        {
            host.DetachScene(expectedWorld, scene);
            return;
        }

        _loadedScene = scene;
        _loadedHost = host;
        _loadedWorld = expectedWorld;
        _loadedInWorld = true;
    }

    private void DetachIfLoaded(int token)
    {
        if (token != Volatile.Read(ref _loadToken)
            || !_loadedInWorld
            || _loadedScene is not { } scene
            || _loadedWorld is not { } world
            || _loadedHost is not { } host)
            return;

        if (!host.DetachScene(world, scene))
            return;

        _loadedInWorld = false;
        _loadedScene = null;
        _loadedHost = null;
        _loadedWorld = null;
    }

    private static void DispatchToAppThread(Action action, string reason)
        => RuntimeThreadServices.Current.InvokeOnAppThread(
            action,
            reason,
            executeNowIfAlreadyAppThread: true);
}
