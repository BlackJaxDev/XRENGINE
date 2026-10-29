namespace XREngine.Rendering.UI;

/// <summary>Holds an optional platform paragraph analyzer.</summary>
public static class UITextParagraphAnalyzerRegistry
{
    private static IUITextParagraphAnalyzer? _current;

    public static IUITextParagraphAnalyzer? Current
    {
        get => System.Threading.Volatile.Read(ref _current);
        set
        {
            IUITextParagraphAnalyzer? previous = System.Threading.Interlocked.Exchange(ref _current, value);
            if (!ReferenceEquals(previous, value))
                UITextShapingBackendRegistry.InvalidateCapturedRequests();
        }
    }
}
