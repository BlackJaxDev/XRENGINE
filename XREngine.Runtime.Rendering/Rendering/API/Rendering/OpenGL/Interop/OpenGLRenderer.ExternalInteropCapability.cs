namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IOpenGlExternalInteropBackendCapability
{
    bool IOpenGlExternalInteropBackendCapability.HasExternalMemory => EXTMemoryObject is not null;
    bool IOpenGlExternalInteropBackendCapability.HasExternalMemoryWin32 => EXTMemoryObjectWin32 is not null;
    bool IOpenGlExternalInteropBackendCapability.HasExternalSemaphore => EXTSemaphore is not null;
    bool IOpenGlExternalInteropBackendCapability.HasExternalSemaphoreWin32 => EXTSemaphoreWin32 is not null;
}
