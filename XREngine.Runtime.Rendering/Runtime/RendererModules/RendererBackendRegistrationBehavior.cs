namespace XREngine.Rendering;

/// <summary>
/// Controls how a catalog handles an already registered backend identifier.
/// </summary>
public enum RendererBackendRegistrationBehavior
{
    RejectDuplicate = 0,
    ReplaceExisting,
}
