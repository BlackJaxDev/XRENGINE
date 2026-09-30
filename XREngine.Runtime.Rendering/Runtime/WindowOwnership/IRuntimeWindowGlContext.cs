namespace XREngine.Rendering;

/// <summary>Borrowed OpenGL context operations; the desktop window remains their lifetime owner.</summary>
public interface IRuntimeWindowGlContext
{
    nint ContextHandle { get; }
    nint DeviceContextHandle { get; }
    long OwnerGeneration { get; }
    /// <summary>Resolves a function on the owning thread, returning zero if the context does not expose it.</summary>
    nint GetProcAddress(string name);
    void MakeCurrent();
    void ClearCurrent();
    void SwapBuffers();
    void SetSwapInterval(int interval);
    void AssertOwnerThread();
}
