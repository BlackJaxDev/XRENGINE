#version 460

layout (location = 0) in vec3 Position;
layout (location = 1) in vec3 Normal;
layout (location = 2) in vec2 TexCoord0;

// All glyph transforms and UVs concatenated across all visible text components (explicit set = 0 for Vulkan)
layout(std430, set = 0, binding = 0) buffer GlyphTransformsBuffer
{
    vec4 GlyphTransforms[];
};

layout(std430, set = 0, binding = 1) buffer GlyphTexCoordsBuffer
{
    vec4 GlyphTexCoords[];
};

// Per-text-component metadata: stored as 6 consecutive vec4s per text instance
// [0..3] = model matrix rows (row-major)
// [4]    = text color (rgba)
// [5]    = UI bounds (x, y, w, h)
layout(std430, set = 0, binding = 2) buffer TextInstanceBuffer
{
    vec4 TextInstances[];
};

// Maps each glyph instance to its parent text index
layout(std430, set = 0, binding = 3) buffer GlyphTextIndexBuffer
{
    uint GlyphTextIndex[];
};

uniform mat4 InverseViewMatrix_VTX;
uniform mat4 ProjMatrix_VTX;

layout (location = 0) out vec3 FragPos;
layout (location = 1) out vec3 FragNorm;
layout (location = 4) out vec2 FragUV0;
layout (location = 5) flat out vec4 InstanceTextColor;

mat4 getTextModelMatrix(uint textIndex)
{
    uint base6 = textIndex * 6u;
    vec4 row0 = TextInstances[base6 + 0u];
    vec4 row1 = TextInstances[base6 + 1u];
    vec4 row2 = TextInstances[base6 + 2u];
    vec4 row3 = TextInstances[base6 + 3u];
    // C# Matrix4x4 is row-major with row-vector semantics.
    // GLSL mat4 uses column-major storage with column-vector semantics.
    // Build columns directly from the serialized rows to apply the required transpose.
    return mat4(row0, row1, row2, row3);
}

void main()
{
    uint textIdx = GlyphTextIndex[gl_InstanceID];
    mat4 modelMatrix = getTextModelMatrix(textIdx);

    uint base6 = textIdx * 6u;
    vec4 textColor = TextInstances[base6 + 4u];
    // vec4 uiBounds = TextInstances[base6 + 5u]; // available if needed

    vec4 tfm = GlyphTransforms[gl_InstanceID];
    vec4 uv = GlyphTexCoords[gl_InstanceID];

    mat4 ViewMatrix = inverse(InverseViewMatrix_VTX);
    mat4 mvpMatrix = ProjMatrix_VTX * ViewMatrix * modelMatrix;

    // Position the glyph quad: tfm.xy = offset, tfm.zw = size
    vec4 position = vec4(tfm.xy + (TexCoord0.xy * tfm.zw), 0.0, 1.0);

    FragPos = (mvpMatrix * position).xyz;
    gl_Position = mvpMatrix * position;
    FragNorm = Normal;
    // Interpolate UVs within the glyph's atlas region
    FragUV0 = mix(uv.xy, uv.zw, Position.xy);
    InstanceTextColor = textColor;
}
