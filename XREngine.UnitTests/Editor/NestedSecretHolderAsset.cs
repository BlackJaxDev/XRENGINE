using XREngine.Core.Files;

namespace XREngine.UnitTests.Editor;

/// <summary>An asset that holds a nested object with a secret member, for the nested secret tests of the publish.</summary>
public sealed class NestedSecretHolderAsset : XRAsset
{
    /// <summary>The nested object with the secret.</summary>
    public NestedSecretOptions Options { get; set; } = new();
}
