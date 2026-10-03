using Silk.NET.Core.Contexts;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>Exposes a test-owned Silk GL context through the renderer's borrowed-context contract.</summary>
internal sealed class SilkGlContextTestAdapter(IGLContext context) : IRuntimeWindowGlContext
{
    public nint ContextHandle => context.Handle;
    public nint DeviceContextHandle => 0;
    public long OwnerGeneration => 0;
    public nint GetProcAddress(string name) => context.GetProcAddress(name);
    public void MakeCurrent() => context.MakeCurrent();
    public void ClearCurrent() => context.Clear();
    public void SwapBuffers() => context.SwapBuffers();
    public void SetSwapInterval(int interval) => context.SwapInterval(interval);
    public void AssertOwnerThread() { }
}
