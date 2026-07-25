namespace XREngine.Rendering;

/// <summary>
/// Stable context supplied while a backend generation cooperatively prepares for unload.
/// </summary>
public readonly record struct RendererModuleUnloadContext(
    RendererBackendId BackendId,
    long Generation,
    string Reason,
    TimeSpan Timeout);

