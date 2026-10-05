namespace XREngine.Core.Files;

/// <summary>
/// Optional hook invoked after a cooked binary object has been fully deserialized.
/// Ancestor deserialization may still suppress property notifications. Types must explicitly rebuild
/// internal invariants that normally rely on property-changed callbacks.
/// </summary>
public interface IPostCookedBinaryDeserialize
{
    void OnPostCookedBinaryDeserialize();
}
