namespace XREngine.Rendering.Commands;

/// <summary>
/// A bounded resource reservation held by a command collection from collection
/// until reset. Executed GPU work must acquire a separate completion-owned retain.
/// </summary>
internal interface IRenderCommandCollectedResource
{
    bool TryRetainCollectedResource();
    void ReleaseCollectedResource();
}
