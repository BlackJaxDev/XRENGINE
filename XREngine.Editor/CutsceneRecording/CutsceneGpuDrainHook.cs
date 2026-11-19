using XREngine.Rendering;

namespace XREngine.Editor.CutsceneRecording;

/// <summary>
/// Waits for the GPU to finish its submitted work at the end of each render of the recording window. The engine timer
/// measures the time of each render dispatch, and <c>EngineTimer.MaxRenderBusyFraction</c> waits for a time that
/// matches it. A backend that keeps several frames in flight returns from a dispatch before the GPU finishes, so a
/// GPU-bound frame looks short and the limit has no effect. This wait puts the GPU time of the previous frame into the
/// dispatch time, so the busy limit follows the real GPU load. The recorder is not real time, so the lost overlap
/// between CPU and GPU work does not matter.
/// </summary>
internal sealed class CutsceneGpuDrainHook : IDisposable
{
    private readonly XRWindow _window;
    private readonly Action _drain;
    private bool _disposed;

    public CutsceneGpuDrainHook(XRWindow window)
    {
        _window = window;
        _drain = Drain;
        _window.PostRenderViewportsCallback += _drain;
    }

    private void Drain()
        => _window.Renderer.WaitForGpu();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _window.PostRenderViewportsCallback -= _drain;
    }
}
