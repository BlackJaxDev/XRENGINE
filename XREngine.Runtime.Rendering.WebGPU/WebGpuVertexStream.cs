using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Resolves cooked shader semantics to an existing engine vertex stream without repacking.</summary>
internal sealed record WebGpuVertexStream(WebGpuDataBuffer Buffer, int Stride, string StepMode)
{
    public List<ShaderVertexAttribute> Attributes { get; } = [];
}
