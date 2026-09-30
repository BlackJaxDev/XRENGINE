namespace XREngine.Rendering;

/// <summary>Provides native clipboard access without exposing operating-system handles.</summary>
public interface IRuntimeClipboardServices
{
    string? GetText();
    void SetText(string text);
}
