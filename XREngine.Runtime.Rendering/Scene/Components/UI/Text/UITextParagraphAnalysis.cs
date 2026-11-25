namespace XREngine.Rendering.UI;

/// <summary>Owned paragraph ranges and line-break opportunities.</summary>
public sealed class UITextParagraphAnalysis
{
    private readonly UITextParagraphRun[] _runs;
    private readonly UITextLineBreakpoint[] _breakpoints;

    public UITextParagraphAnalysis(UITextParagraphRun[] runs, UITextLineBreakpoint[] breakpoints)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(breakpoints);
        int covered = 0;
        foreach (UITextParagraphRun run in runs)
        {
            if (run.Start != covered || run.Length <= 0 || run.Length > breakpoints.Length - covered)
                throw new ArgumentException("Paragraph runs must cover consecutive UTF-16 units.", nameof(runs));
            covered += run.Length;
        }
        if (covered != breakpoints.Length)
            throw new ArgumentException("Paragraph runs must cover every breakpoint.", nameof(runs));
        foreach (UITextLineBreakpoint breakpoint in breakpoints)
            if (!Enum.IsDefined(breakpoint.Before) || !Enum.IsDefined(breakpoint.After))
                throw new ArgumentException("A line-break condition is invalid.", nameof(breakpoints));
        _runs = (UITextParagraphRun[])runs.Clone();
        _breakpoints = (UITextLineBreakpoint[])breakpoints.Clone();
    }

    public ReadOnlyMemory<UITextParagraphRun> Runs => _runs;
    public ReadOnlyMemory<UITextLineBreakpoint> Breakpoints => _breakpoints;
}
