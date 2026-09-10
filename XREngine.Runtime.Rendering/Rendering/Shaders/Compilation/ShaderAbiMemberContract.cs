namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// One physical member in an explicit uniform-buffer ABI.
/// </summary>
public sealed record ShaderAbiMemberContract(
    string PhysicalName,
    string ProviderName,
    uint Offset,
    uint Size,
    string PhysicalType,
    uint ArrayCount = 0,
    uint ArrayStride = 0,
    ShaderAbiMatrixOrder MatrixOrder = ShaderAbiMatrixOrder.None,
    uint MatrixStride = 0,
    string? CpuFieldName = null);
