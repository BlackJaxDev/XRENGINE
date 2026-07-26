namespace XREngine.Rendering;

/// <summary>
/// Reports OpenGL external-memory and semaphore support without exposing extension objects.
/// </summary>
public interface IOpenGlExternalInteropBackendCapability
{
    bool HasExternalMemory { get; }
    bool HasExternalMemoryWin32 { get; }
    bool HasExternalSemaphore { get; }
    bool HasExternalSemaphoreWin32 { get; }
}
