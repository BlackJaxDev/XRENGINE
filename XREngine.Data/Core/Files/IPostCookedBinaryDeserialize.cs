namespace XREngine.Core.Files;

/// <summary>
/// Optional hook invoked after a cooked binary object has been fully deserialized.
/// This runs after property notifications are re-enabled, so types can rebuild
/// internal invariants that normally rely on property-changed callbacks.
/// </summary>
public interface IPostCookedBinaryDeserialize
{
    void OnPostCookedBinaryDeserialize();
}
