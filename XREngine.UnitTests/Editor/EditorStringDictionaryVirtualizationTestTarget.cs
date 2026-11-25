namespace XREngine.UnitTests.Editor;

internal sealed class EditorStringDictionaryVirtualizationTestTarget
{
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
}
