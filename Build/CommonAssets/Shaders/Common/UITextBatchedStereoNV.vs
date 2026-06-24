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
uniform int TextRenderLayer_VTX;

layout (location = 0) out vec3 FragPos;
layout (location = 1) out vec3 FragNorm;
layout (location = 4) out vec2 FragUV0;
layout (location = 5) flat out vec4 InstanceTextColor;
layout (location = 6) flat out vec4 GlyphUVBounds;
layout (location = 7) flat out vec4 InstanceOutlineColor;
layout (location = 8) flat out vec4 InstanceOutlineParams;

const uint TextInstanceStride = 8u;
const int TextRenderLayerFill = 2;

mat4 getTextModelMatrix(uint textIndex)
{
    uint baseIndex = textIndex * TextInstanceStride;
    vec4 row0 = TextInstances[baseIndex + 0u];
    vec4 row1 = TextInstances[baseIndex + 1u];
    vec4 row2 = TextInstances[baseIndex + 2u];
    vec4 row3 = TextInstances[baseIndex + 3u];
    return mat4(row0, row1, row2, row3);
}

void main()
{
    vec2 corner = Position.xy;

    if (TextDebugMode == 3)
    {
        vec2 ndc = vec2(-0.94, 0.74) + corner * vec2(0.44, 0.18);
        FragPos = vec3(ndc, 0.0);
        gl_Position = vec4(ndc, 0.0, 1.0);
        gl_SecondaryPositionNV = vec4(ndc, 0.0, 1.0);
        gl_Layer = 0;
        FragNorm = Normal;
        FragUV0 = corner;
        InstanceTextColor = vec4(0.0, 1.0, 1.0, 1.0);
        GlyphUVBounds = vec4(0.0, 0.0, 1.0, 1.0);
        InstanceOutlineColor = vec4(0.0);
        InstanceOutlineParams = vec4(0.0);
        return;
    }

    uint textIdx = GlyphTextIndex[gl_InstanceID];
    mat4 modelMatrix = getTextModelMatrix(textIdx);

    uint baseIndex = textIdx * TextInstanceStride;
    vec4 textColor = TextInstances[baseIndex + 4u];
    vec4 outlineColor = TextInstances[baseIndex + 6u];
    vec4 outlineParams = TextInstances[baseIndex + 7u];

    vec4 tfm = GlyphTransforms[gl_InstanceID];
    vec4 uv = GlyphTexCoords[gl_InstanceID];

    vec2 glyphMin = tfm.xy;
    vec2 glyphSize = tfm.zw;
    vec2 uvMin = uv.xy;
    vec2 uvMax = uv.zw;
    if (TextRenderLayer_VTX != TextRenderLayerFill && outlineParams.x > 0.0 && outlineColor.a > 0.0)
    {
        vec2 expand = vec2(outlineParams.x);
        vec2 uvExpand = (uvMax - uvMin) * (expand / max(abs(glyphSize), vec2(1e-6)));
        vec2 glyphDirection = vec2(
            glyphSize.x < 0.0 ? -1.0 : 1.0,
            glyphSize.y < 0.0 ? -1.0 : 1.0);
        glyphMin -= expand * glyphDirection;
        glyphSize += expand * 2.0 * glyphDirection;
        uvMin -= uvExpand;
        uvMax += uvExpand;
    }

    vec4 position = vec4(glyphMin + (corner * glyphSize), 0.0, 1.0);

    mat4 leftMvpMatrix = LeftEyeViewProjectionMatrix_VTX * modelMatrix;
    mat4 rightMvpMatrix = RightEyeViewProjectionMatrix_VTX * modelMatrix;

    FragPos = (leftMvpMatrix * position).xyz;
    gl_Position = leftMvpMatrix * position;
    gl_SecondaryPositionNV = rightMvpMatrix * position;
    gl_Layer = 0;
    FragNorm = Normal;
    FragUV0 = mix(uvMin, uvMax, corner);
    InstanceTextColor = textColor;
    GlyphUVBounds = uv;
    InstanceOutlineColor = outlineColor;
    InstanceOutlineParams = outlineParams;
}
