#version 460
#extension GL_OVR_multiview2 : require
//#extension GL_EXT_multiview_tessellation_geometry_shader : enable

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

uniform mat4 ModelMatrix;
uniform mat4 LeftEyeViewProjectionMatrix_VTX;
uniform mat4 RightEyeViewProjectionMatrix_VTX;

layout (location = 0) out vec3 FragPos;
layout (location = 1) out vec3 FragNorm;
layout (location = 4) out vec2 FragUV0;
layout (location = 5) flat out vec4 GlyphUVBounds;
layout (location = 20) out vec3 FragPosLocal;

out gl_PerVertex
{
	vec4 gl_Position;
	float gl_PointSize;
	float gl_ClipDistance[];
};

void main()
{
    vec4 tfm = GlyphTransforms[gl_InstanceID];
    vec4 uv = GlyphTexCoords[gl_InstanceID];
	bool left = gl_ViewID_OVR == 0;
	mat4 mvpMatrix = (left ? LeftEyeViewProjectionMatrix_VTX : RightEyeViewProjectionMatrix_VTX) * ModelMatrix;
	
	vec4 position = vec4((tfm.xy + (TexCoord0.xy * tfm.zw)), 0.0f, 1.0f);
	
	FragPosLocal = position.xyz;
	FragPos = (mvpMatrix * position).xyz;
	gl_Position = mvpMatrix * position;
	FragNorm = Normal;
	FragUV0 = mix(uv.xy, uv.zw, Position.xy);
	GlyphUVBounds = uv;
}