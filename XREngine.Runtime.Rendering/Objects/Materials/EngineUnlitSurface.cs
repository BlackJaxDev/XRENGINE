using System.Numerics;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>Typed inputs of the five canonical forward unlit fragments.</summary>
public readonly record struct EngineUnlitSurface(
    EngineMaterialSemanticIdentity Semantic,
    Vector4 Color,
    XRTexture? Texture,
    MaterialSurfaceTextureBinding? TextureBinding,
    float AlphaCutoff,
    ETransparencyMode TransparencyMode);
