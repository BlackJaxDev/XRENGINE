using XREngine.Rendering;

namespace XREngine;

internal sealed class NullRuntimeVrLifecycleServices : IRuntimeVrLifecycleServices
{
    public static NullRuntimeVrLifecycleServices Instance { get; } = new();

    public bool InitializeOpenXR(XRWindow? window) => throw MissingService(nameof(InitializeOpenXR));
    public bool StopOpenXR() => throw MissingService(nameof(StopOpenXR));
    public Task<bool> InitializeLocal(IRuntimeOpenVrActionManifest actionManifest, RuntimeOpenVrApplicationManifest vrManifest, XRWindow window)
        => throw MissingService(nameof(InitializeLocal));
    public void InitRenderEmulated(XRWindow window) => throw MissingService(nameof(InitRenderEmulated));
    public Task<bool> InitializeClient(IRuntimeOpenVrActionManifest actionManifest, RuntimeOpenVrApplicationManifest vrManifest)
        => throw MissingService(nameof(InitializeClient));
    public bool InitializeServer() => throw MissingService(nameof(InitializeServer));
    public void StartInputClient() => throw MissingService(nameof(StartInputClient));
    public void StopInputServer() => throw MissingService(nameof(StopInputServer));
    public Task SendInputs() => Task.CompletedTask;

    private static InvalidOperationException MissingService(string operation)
        => new($"VR lifecycle service is not installed; {operation} requires desktop VR composition.");
}

