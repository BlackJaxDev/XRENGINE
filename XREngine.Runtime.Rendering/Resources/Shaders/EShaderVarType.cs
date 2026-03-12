namespace XREngine.Rendering.Models.Materials;

/// <summary>
/// Literal GLSL type names with a _ appended to the front.
/// Must match the type names in GLSL.
/// </summary>
public enum EShaderVarType
{
    _bool,
    _int,
    _uint,
    _float,
    _double,
    _vec2,
    _vec3,
    _vec4,
    _mat3,
    _mat4,
    _ivec2,
    _ivec3,
    _ivec4,
    _uvec2,
    _uvec3,
    _uvec4,
    _dvec2,
    _dvec3,
    _dvec4,
    _bvec2,
    _bvec3,
    _bvec4,
    _sampler2D
}
