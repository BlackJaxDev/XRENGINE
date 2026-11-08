using XREngine.Core.Files;

namespace XREngine.UnitTests.Editor;

/// <summary>An asset that holds secret-bearing objects in an engine list, for the nested secret tests of the publish.</summary>
public sealed class NestedSecretListHolderAsset : XRAsset
{
    /// <summary>The list items, each with a secret.</summary>
    public EventList<NestedSecretOptions> Items { get; set; } = new();
}
