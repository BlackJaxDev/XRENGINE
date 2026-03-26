#version 460
#extension GL_OVR_multiview2 : require

layout (location = 0) in vec3 Position;
layout (location = 1) in vec3 Normal;
layout (location = 2) in vec2 TexCoord0;

layout(std430, set = 0, binding = 0) buffer GlyphTransformsBuffer
{
    vec4 GlyphTransforms[];
};

layout(std430, set = 0, binding = 1) buffer GlyphTexCoordsBuffer
{
    vec4 GlyphTexCoords[];
};

layout(std430, set = 0, binding = 2) buffer TextInstanceBuffer
{
    vec4 TextInstances[];
};

layout(std430, set = 0, binding = 3) buffer GlyphTextIndexBuffer
{
    uint GlyphTextIndex[];
};

uniform mat4 LeftEyeViewProjectionMatrix_VTX;
uniform mat4 RightEyeViewProjectionMatrix_VTX;

layout (location = 0) out vec3 FragPos;
layout (location = 1) out vec3 FragNorm;
layout (location = 4) out vec2 FragUV0;
layout (location = 5) flat out vec4 InstanceTextColor;

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
    return mat4(row0, row1, row2, row3);
}

void main()
{
    uint textIdx = GlyphTextIndex[gl_InstanceID];
    mat4 modelMatrix = getTextModelMatrix(textIdx);

    uint base6 = textIdx * 6u;
    vec4 textColor = TextInstances[base6 + 4u];

    vec4 tfm = GlyphTransforms[gl_InstanceID];
    vec4 uv = GlyphTexCoords[gl_InstanceID];

    bool leftEye = gl_ViewID_OVR == 0;
    mat4 mvpMatrix = (leftEye ? LeftEyeViewProjectionMatrix_VTX : RightEyeViewProjectionMatrix_VTX) * modelMatrix;

    vec4 position = vec4(tfm.xy + (TexCoord0.xy * tfm.zw), 0.0, 1.0);

    FragPos = (mvpMatrix * position).xyz;
    gl_Position = mvpMatrix * position;
    FragNorm = Normal;
    FragUV0 = mix(uv.xy, uv.zw, Position.xy);
    InstanceTextColor = textColor;
}