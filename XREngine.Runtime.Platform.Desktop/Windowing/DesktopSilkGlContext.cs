using Silk.NET.Windowing;
using Silk.NET.Core.Contexts;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Borrowed GL operations tied to one live Silk window generation.</summary>
internal sealed class DesktopSilkGlContext(IWindow window, int windowOwnerThreadId, long ownerGeneration)
    : IRuntimeWindowGlContext
{
    private int _graphicsOwnerThreadId = windowOwnerThreadId;
    private int _retired;
    private readonly nint _contextHandle = window.GLContext?.Handle ?? 0;
    private readonly nint _deviceContextHandle = window.Native?.Win32?.HDC ?? 0;

    internal IGLContext? SilkContext => window.GLContext;
    public nint ContextHandle => _contextHandle;
    public nint DeviceContextHandle => _deviceContextHandle;
    public long OwnerGeneration { get; } = ownerGeneration;

    public nint GetProcAddress(string name)
    {
        AssertOwnerThread();
        // Optional extension probes must report absence without aborting renderer creation.
        return window.GLContext is INativeContext nativeContext
            && nativeContext.TryGetProcAddress(name, out nint address)
            ? address
            : 0;
    }

    public void MakeCurrent()
    {
        ThrowIfRetired();
        int current = Environment.CurrentManagedThreadId;
        int previous = Interlocked.CompareExchange(ref _graphicsOwnerThreadId, current, 0);
        if (previous != 0 && previous != current)
            throw new InvalidOperationException("The desktop GL context is current on another thread. Detach it before transfer.");
        window.MakeCurrent();
    }

    public void ClearCurrent()
    {
        AssertOwnerThread();
        window.GLContext?.Clear();
        Volatile.Write(ref _graphicsOwnerThreadId, 0);
    }

    public void SwapBuffers()
    {
        AssertOwnerThread();
        window.GLContext?.SwapBuffers();
    }

    public void SetSwapInterval(int interval)
    {
        AssertOwnerThread();
        window.GLContext?.SwapInterval(interval);
    }

    public void AssertOwnerThread()
    {
        ThrowIfRetired();
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref _graphicsOwnerThreadId))
            throw new InvalidOperationException("The desktop GL context is being used off its graphics owner thread.");
    }

    internal void Retire() => Volatile.Write(ref _retired, 1);

    private void ThrowIfRetired()
    {
        if (Volatile.Read(ref _retired) != 0)
            throw new ObjectDisposedException(nameof(DesktopSilkGlContext));
    }
}
