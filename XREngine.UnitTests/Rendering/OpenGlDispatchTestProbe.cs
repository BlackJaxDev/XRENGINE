using System.Reflection;
using XREngine.Data.Vectors;
using XREngine.Input.Devices;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>Records window dispatch ordering without creating a native window.</summary>
public class OpenGlDispatchTestProbe : DispatchProxy, IRuntimeWindowEventSink, IRuntimeWindowGlContext
{
    public readonly List<string> Calls = [];
    public bool AutomaticSwap = true;
    public bool PermitSwap = true;
    public Action? Render;
    public Action? Verdict;
    public Exception? SwapFailure;
    public ulong CompletedToken;
    public bool Presented;
    public bool AutomaticSwapDuringRender;
    public bool AutomaticSwapAtCompletion;

    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        switch (method!.Name)
        {
            case "get_ShouldSwapAutomatically": return AutomaticSwap;
            case "set_ShouldSwapAutomatically": AutomaticSwap = (bool)arguments![0]!; return null;
            case "DoRender":
                Calls.Add("render");
                AutomaticSwapDuringRender = AutomaticSwap;
                Render?.Invoke();
                return null;
            default: throw new NotSupportedException(method.Name);
        }
    }

    public void Flush() => Calls.Add("flush");
    public ulong BeginOpenGlRenderDispatch() { Calls.Add("begin"); return 17UL; }
    public bool CanPresentOpenGlRenderDispatch(ulong token)
    {
        Calls.Add("verdict");
        Verdict?.Invoke();
        return token == 17UL && PermitSwap;
    }
    public void CompleteOpenGlRenderDispatch(ulong token, bool presented)
    {
        Calls.Add("complete");
        CompletedToken = token;
        Presented = presented;
        AutomaticSwapAtCompletion = AutomaticSwap;
    }
    public void SwapBuffers()
    {
        Calls.Add("swap");
        if (SwapFailure is { } failure)
            throw failure;
    }
    public nint ContextHandle => 0;
    public nint DeviceContextHandle => 0;
    public long OwnerGeneration => 1;
    public nint GetProcAddress(string name) => throw new NotSupportedException(name);
    public void MakeCurrent() { }
    public void ClearCurrent() { }
    public void SetSwapInterval(int interval) { }
    public void AssertOwnerThread() { }
    public void SurfaceChanged(WindowSurfaceSnapshot snapshot) { }
    public void FocusChanged(bool focused) { }
    public void FileDropped(string[] paths) { }
    public void KeyDown(EKey key) { }
    public bool CloseRequested() => false;
    public void InteractiveResizeStarted() { }
    public void InteractiveResizeUpdated(IVector2 framebufferExtent) { }
    public void InteractiveResizeEnded() { }
    public void RepaintRequested() { }
    public void RenderRequested(double deltaSeconds) { }
}
