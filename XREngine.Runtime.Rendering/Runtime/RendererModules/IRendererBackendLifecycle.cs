namespace XREngine.Rendering;

/// <summary>
/// Optional registration lifecycle implemented by modules that own reloadable resources.
/// </summary>
public interface IRendererBackendLifecycle
{
    /// <summary>
    /// Called immediately before a registration becomes visible in a catalog.
    /// </summary>
    void OnRegistered();

    /// <summary>
    /// Called once after a registration stops being visible in a catalog.
    /// </summary>
    void OnUnregistered();
}
