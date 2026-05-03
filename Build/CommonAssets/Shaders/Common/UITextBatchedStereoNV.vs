#version 460
#extension GL_NV_viewport_array2 : require
#extension GL_NV_stereo_view_rendering : require

layout(secondary_view_offset = 1) out highp int gl_Layer;

layout (location = 0) in vec3 Position;
layout (location = 1) in vec3 Normal;
layout (location = 2) in vec2 TexCoord0;

layout(std430, binding = 0) buffer GlyphTransformsBuffer
{
    vec4 GlyphTransforms[];
};

layout(std430, binding = 1) buffer GlyphTexCoordsBuffer
{
    vec4 GlyphTexCoords[];
};

layout(std430, binding = 2) buffer TextInstanceBuffer
{
    vec4 TextInstances[];
};

layout(std430, binding = 3) buffer GlyphTextIndexBuffer
{
    uint GlyphTextIndex[];
};

uniform mat4 LeftEyeViewProjectionMatrix_VTX;
uniform mat4 RightEyeViewProjectionMatrix_VTX;
uniform int TextDebugMode;

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
    return mat4(row0, row1, row2, row3);
}

void main()
{
    if (TextDebugMode == 3)
    {
        vec2 corner = TexCoord0.xy;
        vec2 ndc = vec2(-0.94, 0.74) + corner * vec2(0.44, 0.18);
        FragPos = vec3(ndc, 0.0);
        gl_Position = vec4(ndc, 0.0, 1.0);
        gl_SecondaryPositionNV = vec4(ndc, 0.0, 1.0);
        gl_Layer = 0;
        FragNorm = Normal;
        FragUV0 = corner;
        InstanceTextColor = vec4(0.0, 1.0, 1.0, 1.0);
        return;
    }

    uint textIdx = GlyphTextIndex[gl_InstanceID];
    mat4 modelMatrix = getTextModelMatrix(textIdx);

    uint base6 = textIdx * 6u;
    vec4 textColor = TextInstances[base6 + 4u];

    vec4 tfm = GlyphTransforms[gl_InstanceID];
    vec4 uv = GlyphTexCoords[gl_InstanceID];

    vec4 position = vec4(tfm.xy + (TexCoord0.xy * tfm.zw), 0.0, 1.0);

    mat4 leftMvpMatrix = LeftEyeViewProjectionMatrix_VTX * modelMatrix;
    mat4 rightMvpMatrix = RightEyeViewProjectionMatrix_VTX * modelMatrix;

    FragPos = (leftMvpMatrix * position).xyz;
    gl_Position = leftMvpMatrix * position;
    gl_SecondaryPositionNV = rightMvpMatrix * position;
    gl_Layer = 0;
    FragNorm = Normal;
    FragUV0 = mix(uv.xy, uv.zw, Position.xy);
    InstanceTextColor = textColor;
}
