namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Holds the raw text of one ShaderLab program or include block.
/// </summary>
/// <param name="Language">The block language marker.</param>
/// <param name="IsInclude">True for an include block, which other program blocks in the same scope receive.</param>
/// <param name="Code">The text between the start and end markers, unchanged.</param>
/// <param name="FirstLine">The one-based source line of the first character of <paramref name="Code"/>.</param>
public sealed record ShaderLabProgramBlock(ShaderLabProgramLanguage Language, bool IsInclude, string Code, int FirstLine);
