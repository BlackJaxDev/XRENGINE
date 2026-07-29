using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

public readonly record struct AutoUniformMember(
    string Name,
    string GlslType,
    EShaderVarType? EngineType,
    bool IsArray,
    uint ArrayLength,
    uint ArrayStride,
    uint Offset,
    uint Size,
    AutoUniformDefaultValue? DefaultValue,
    IReadOnlyList<AutoUniformDefaultValue>? DefaultArrayValues,
    IReadOnlyList<AutoUniformMember>? StructMembers = null);
