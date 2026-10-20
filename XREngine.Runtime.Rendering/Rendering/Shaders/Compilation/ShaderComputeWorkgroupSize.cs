namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Hash-bound local invocation dimensions verified against a cooked compute entry.</summary>
public readonly record struct ShaderComputeWorkgroupSize(uint X, uint Y, uint Z);
