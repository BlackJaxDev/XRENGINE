namespace XREngine.Rendering;

/// <summary>Creates an uninitialized native backend on its owning window thread.</summary>
public interface IRuntimeWindowBackendFactory
{
    IRuntimeWindowBackend Create(in RuntimeWindowCreateOptions options);
}
