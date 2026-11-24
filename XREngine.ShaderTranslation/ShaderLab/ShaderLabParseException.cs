namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Reports ShaderLab structure text that the parser cannot read.
/// </summary>
public sealed class ShaderLabParseException(string sourcePath, int line, string message)
    : Exception($"{sourcePath}({line}): {message}")
{
    /// <summary>Gets the source path of the failing file.</summary>
    public string SourcePath { get; } = sourcePath;

    /// <summary>Gets the one-based source line of the failure.</summary>
    public int Line { get; } = line;
}
