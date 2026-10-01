namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Optional application-owned composition invoked after a Core world and its
/// renderer exist, but before the target asset's scenes are loaded.
/// </summary>
public interface IRuntimeWorldHostCompositionServices
{
    void Compose(RuntimeWorldHost host);
}
