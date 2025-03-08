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
uniform mat4 LeftEyeInverseViewMatrix_VTX;
uniform mat4 RigthEyeInverseViewMatrix_VTX;
uniform mat4 LeftEyeProjMatrix_VTX;
uniform mat4 RightEyeProjMatrix_VTX;

layout (location = 0) out vec3 FragPos;
layout (location = 1) out vec3 FragNorm;
layout (location = 4) out vec2 FragUV0;
layout (location = 20) out vec3 FragPosLocal;

out gl_PerVertex
{
	vec4 gl_Position;
	float gl_PointSize;
	float gl_ClipDistance[];
};

mat3 adjoint(mat4 m)
{
	return mat3(
		cross(m[1].xyz, m[2].xyz),
		cross(m[2].xyz, m[0].xyz),
		cross(m[0].xyz, m[1].xyz)
	);
}

const float PI = 3.14f;

void main()
{
    vec4 tfm = GlyphTransforms[gl_InstanceID];
    vec4 uv = GlyphTexCoords[gl_InstanceID];
	bool left = gl_ViewID_OVR == 0;
	mat4 InverseViewMatrix_VTX = left ? LeftEyeInverseViewMatrix_VTX : RigthEyeInverseViewMatrix_VTX;
	mat4 ProjMatrix_VTX = left ? LeftEyeProjMatrix_VTX : RightEyeProjMatrix_VTX;

	mat4 ViewMatrix = inverse(InverseViewMatrix_VTX);
	mat4 mvMatrix = ViewMatrix * ModelMatrix;
	mat4 mvpMatrix = ProjMatrix_VTX * mvMatrix;
	mat4 vpMatrix = ProjMatrix_VTX * ViewMatrix;
	mat3 normalMatrix = adjoint(ModelMatrix);
	
	vec4 position = vec4((tfm.xy + (TexCoord0.xy * tfm.zw)), 0.0f, 1.0f);

	vec3 normal = Normal;
	
	FragPosLocal = position.xyz;
	FragPos = (mvpMatrix * position).xyz;
	gl_Position = mvpMatrix * position;
	FragNorm = normalize(normalMatrix * normal);
	FragUV0 = mix(uv.xy, uv.zw, Position.xy);
}