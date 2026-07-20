namespace XREngine.Components.Scene.Volumes;

/// <summary>
/// Opaque handle for a scene asset loaded by the application host.
/// </summary>
public interface IRuntimeSceneStreamingHandle;

/// <summary>
/// Bridges runtime streaming volumes to application-owned scene assets and worlds.
/// </summary>
public interface IRuntimeSceneStreamingHostServices
{
    Task<IRuntimeSceneStreamingHandle?> LoadSceneAsync(string sceneAssetPath);
    bool AttachScene(IRuntimeWorldContext world, IRuntimeSceneStreamingHandle scene);
    bool DetachScene(IRuntimeWorldContext world, IRuntimeSceneStreamingHandle scene);
}

/// <summary>
/// Process-wide scene-streaming host boundary.
/// </summary>
public static class RuntimeSceneStreamingHostServices
{
    private static IRuntimeSceneStreamingHostServices _current = new NullRuntimeSceneStreamingHostServices();

    public static IRuntimeSceneStreamingHostServices Current
    {
        get => _current;
        set => _current = value ?? new NullRuntimeSceneStreamingHostServices();
    }

    private sealed class NullRuntimeSceneStreamingHostServices : IRuntimeSceneStreamingHostServices
    {
        public Task<IRuntimeSceneStreamingHandle?> LoadSceneAsync(string sceneAssetPath)
            => Task.FromResult<IRuntimeSceneStreamingHandle?>(null);

        public bool AttachScene(IRuntimeWorldContext world, IRuntimeSceneStreamingHandle scene)
            => false;

        public bool DetachScene(IRuntimeWorldContext world, IRuntimeSceneStreamingHandle scene)
            => false;
    }
}
