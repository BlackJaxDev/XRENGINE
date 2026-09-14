namespace XREngine.Rendering;

/// <summary>Cached metadata handle owned by a linked backend program.</summary>
public readonly record struct ShaderCommandCoverageProgramToken(int EntryIndex, EShaderType Stage);
