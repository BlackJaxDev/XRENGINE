namespace XREngine.Editor.UI.Components;

internal sealed class ConsoleCachedEntry(ulong id, int repeatCount, string text)
{
    public ulong Id = id;
    public int RepeatCount = repeatCount;
    public string Text = text;
}
