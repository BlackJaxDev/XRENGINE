namespace XREngine.Data;

/// <summary>Loads optional engine assemblies from host storage for legacy type resolution.</summary>
public interface IRuntimeAssemblyLoader
{
    /// <summary>Attempts the host's one-time runtime assembly discovery.</summary>
    void EnsureRuntimeAssembliesLoaded();
}
