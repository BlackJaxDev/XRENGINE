namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Holds the exit code and output text of one external tool run.
/// </summary>
public sealed record ExternalToolResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Returns standard error and standard output joined, without blank edges.</summary>
    public string CombinedOutput => (StandardError + Environment.NewLine + StandardOutput).Trim();

    /// <summary>Returns the combined output, or the exit code when the tool wrote nothing.</summary>
    public string FailureText
    {
        get
        {
            string output = CombinedOutput;
            return output.Length > 0 ? output : $"The tool stopped with exit code {ExitCode} and wrote no diagnostics.";
        }
    }
}
