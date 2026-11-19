using System.Text;

namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Orders the diagnostic output of <c>slangc</c> so that errors come first.
/// </summary>
/// <remarks>
/// Unity programs often cause many warnings before the first error, for example for undefined
/// preprocessor identifiers. Reports and logs cut long text, so the error must be at the start.
/// Each diagnostic starts with a line such as <c>error[E30015]: ...</c> or <c>warning[E15205]: ...</c>
/// and continues until the next such line. A note stays with the diagnostic before it.
/// </remarks>
public static class SlangDiagnosticText
{
    /// <summary>Returns the diagnostics with all errors before the other diagnostics. The order in each group does not change.</summary>
    /// <param name="output">The <c>slangc</c> output.</param>
    public static string OrderErrorsFirst(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        StringBuilder errors = new(output.Length);
        StringBuilder others = new(output.Length);
        StringBuilder current = others;
        foreach (ReadOnlySpan<char> line in output.AsSpan().EnumerateLines())
        {
            if (line.StartsWith("error[", StringComparison.Ordinal) || line.StartsWith("error:", StringComparison.Ordinal))
                current = errors;
            else if (line.StartsWith("warning[", StringComparison.Ordinal) ||
                line.StartsWith("warning:", StringComparison.Ordinal))
                current = others;

            current.Append(line).Append('\n');
        }

        if (errors.Length == 0)
            return output;

        return errors.Append(others).ToString().TrimEnd();
    }
}
