namespace XREngine.UnitTests.Editor;

/// <summary>A nested settings object with a secret member, for the nested secret tests of the publish.</summary>
public sealed class NestedSecretOptions
{
    /// <summary>A secret that a publish must not ship.</summary>
    [Secret]
    public string? Token { get; set; }

    /// <summary>A value that is not a secret.</summary>
    public string? Label { get; set; }
}
