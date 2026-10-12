using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

/// <summary>
/// Immutable program and copied input values retained with a canonical material.
/// The verified producer changes local position and normal after skin/morph;
/// topology, tangent, texture coordinates and color remain unchanged.
/// </summary>
public readonly record struct AdvancedNativeVertexMaterial(
    ShaderProgramArtifact? Program,
    AdvancedNativeVertexInputs Inputs)
{
    public bool IsPresent => Program is not null;
}
