namespace XREngine.Rendering.UI;

/// <summary>Analyze immutable UTF-16 paragraph values without retaining request state.</summary>
public interface IUITextParagraphAnalyzer
{
    UITextParagraphAnalysis Analyze(string text, string language, UITextDirection direction);
}
