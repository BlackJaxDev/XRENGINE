using XREngine.Core.Files;

namespace XREngine.UnitTests.Editor;

/// <summary>
/// An asset that holds a secret-bearing object in a member of a base type, which the declared-type walk does not see, for the
/// write-time secret tests of the publish.
/// </summary>
public sealed class PolymorphicSecretHolderAsset : XRAsset
{
    /// <summary>A payload of any type.</summary>
    public object? Payload { get; set; }
}
