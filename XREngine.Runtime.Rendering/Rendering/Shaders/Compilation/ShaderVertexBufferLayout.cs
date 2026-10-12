using System.Collections.Immutable;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Canonical vertex packing; equivalent engine streams may remap slots, strides, and offsets while preserving locations, formats, and semantics.</summary>
public sealed record ShaderVertexBufferLayout(
    int Slot,
    int Stride,
    string StepMode,
    ImmutableArray<ShaderVertexAttribute> Attributes);
