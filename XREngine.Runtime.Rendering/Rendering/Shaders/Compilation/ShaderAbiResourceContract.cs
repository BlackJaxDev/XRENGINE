using System.Collections.Immutable;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Immutable binding and layout contract required from a shader frontend.
/// </summary>
public sealed record ShaderAbiResourceContract(
    string Name,
    string PhysicalName,
    uint Set,
    uint Binding,
    ShaderAbiResourceKind Kind,
    ShaderAbiResourceOwner Owner,
    ShaderAbiFrequency Frequency,
    uint ByteSize,
    ImmutableArray<ShaderAbiMemberContract> Members,
    ShaderAbiDescriptorLifetime? DescriptorLifetime = null);
