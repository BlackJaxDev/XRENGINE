namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Reports a compiler or cross-compiler failure with its diagnostic text.
/// </summary>
public sealed class ShaderCompileException(string tool, string output)
    : Exception($"{tool} failed: {output}")
{
    /// <summary>Gets the tool name.</summary>
    public string Tool { get; } = tool;

    /// <summary>Gets the tool diagnostic output.</summary>
    public string Output { get; } = output;
}
