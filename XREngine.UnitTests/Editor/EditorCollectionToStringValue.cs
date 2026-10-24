namespace XREngine.UnitTests.Editor;

internal sealed class EditorCollectionToStringValue(Func<string> text)
{
    public override string ToString() => text();
}
