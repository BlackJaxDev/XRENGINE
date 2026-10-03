namespace XREngine.Rendering.GI;

/// <summary>Provides the optional native ray tracing bridge for its owning renderer.</summary>
public interface IRestirRayTracingBackend
{
    string? LastFailure { get; }
    bool VerifySupport(bool logSuccess);
    bool TryInitialize();
    bool TryBind(uint pipeline);
    bool TryDispatch(in RestirGI.TraceParameters parameters);
}
