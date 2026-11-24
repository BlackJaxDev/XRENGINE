namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Holds the SPIR-V and reflection of one compiled stage.
/// </summary>
/// <param name="Spirv">The SPIR-V module.</param>
/// <param name="Reflection">The parsed reflection data.</param>
/// <param name="Warnings">The compiler warning text, or an empty string.</param>
public sealed record SlangSpirvOutput(byte[] Spirv, SlangStageReflection Reflection, string Warnings);
