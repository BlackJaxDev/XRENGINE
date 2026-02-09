#version 460

layout (location = 0) in vec3 Position;
layout (location = 1) in vec3 Normal;
layout (location = 2) in vec2 TexCoord0;

// All glyph transforms and UVs concatenated across all visible text components
layout(std430, binding = 0) buffer GlyphTransformsBuffer
{
    vec4 GlyphTransforms[];
};

layout(std430, binding = 1) buffer GlyphTexCoordsBuffer
{
    vec4 GlyphTexCoords[];
};

// Per-text-component metadata: stored as 6 consecutive vec4s per text instance
// [0..3] = model matrix rows (row-major)
// [4]    = text color (rgba)
// [5]    = UI bounds (x, y, w, h)
layout(std430, binding = 2) buffer TextInstanceBuffer
{
    vec4 TextInstances[];
};

// Maps each glyph instance to its parent text index
layout(std430, binding = 3) buffer GlyphTextIndexBuffer
{
    uint GlyphTextIndex[];
};

uniform mat4 InverseViewMatrix_VTX;
uniform mat4 ProjMatrix_VTX;

layout (location = 0) out vec3 FragPos;
layout (location = 1) out vec3 FragNorm;
layout (location = 4) out vec2 FragUV0;
flat out vec4 InstanceTextColor;

out gl_PerVertex
{
    vec4 gl_Position;
    float gl_PointSize;
    float gl_ClipDistance[];
};

mat4 getTextModelMatrix(uint textIndex)
{
    uint base6 = textIndex * 6u;
    vec4 row0 = TextInstances[base6 + 0u];
    vec4 row1 = TextInstances[base6 + 1u];
    vec4 row2 = TextInstances[base6 + 2u];
    vec4 row3 = TextInstances[base6 + 3u];
    return mat4(
        vec4(row0.x, row1.x, row2.x, row3.x),
        vec4(row0.y, row1.y, row2.y, row3.y),
        vec4(row0.z, row1.z, row2.z, row3.z),
        vec4(row0.w, row1.w, row2.w, row3.w)
    );
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
