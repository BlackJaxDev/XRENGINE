using OpenVR.NET.Manifest;
using XREngine.Rendering;

namespace XREngine;

internal sealed class NullRuntimeVrLifecycleServices : IRuntimeVrLifecycleServices
{
    public static NullRuntimeVrLifecycleServices Instance { get; } = new();

    public bool InitializeOpenXR(XRWindow? window) => false;
    public Task<bool> InitializeLocal(IActionManifest actionManifest, VrManifest vrManifest, XRWindow window) => Task.FromResult(false);
    public void InitRenderEmulated(XRWindow window) { }
    public Task<bool> InitializeClient(IActionManifest actionManifest, VrManifest vrManifest) => Task.FromResult(false);
    public bool InitializeServer() => false;
    public void StartInputClient() { }
    public void StopInputServer() { }
    public Task SendInputs() => Task.CompletedTask;
}
