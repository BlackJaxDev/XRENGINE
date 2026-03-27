// Uber Shader - Vertex Shader (NV Stereo)

#version 450 core
#extension GL_NV_viewport_array2 : require
#extension GL_NV_stereo_view_rendering : require

layout(secondary_view_offset = 1) out highp int gl_Layer;

out gl_PerVertex {
	vec4 gl_Position;
	float gl_PointSize;
	float gl_ClipDistance[];
};

// ============================================
// Vertex Inputs
// ============================================
layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec4 Tangent;
layout(location = 3) in vec2 TexCoord0;
layout(location = 4) in vec2 TexCoord1;
layout(location = 5) in vec2 TexCoord2;
layout(location = 6) in vec2 TexCoord3;
layout(location = 7) in vec4 Color0;

// ============================================
// Vertex Outputs
// ============================================
layout(location = 0) out vec4 v_Uv01;
layout(location = 1) out vec4 v_Uv23;
layout(location = 2) out vec3 v_WorldPos;
layout(location = 3) out vec3 v_WorldNormal;
layout(location = 4) out vec3 v_WorldTangent;
layout(location = 5) out float v_TangentSign;
layout(location = 6) out vec4 v_VertexColor;
layout(location = 7) out vec3 v_LocalPos;
layout(location = 8) out vec3 v_ViewDir;
layout(location = 22) out float FragViewIndex;

// ============================================
// Uniforms
// ============================================
#include "uniforms.glsl"

uniform mat4 LeftEyeViewMatrix_VTX;
uniform mat4 RightEyeViewMatrix_VTX;
uniform mat4 LeftEyeInverseViewMatrix_VTX;
uniform mat4 RightEyeInverseViewMatrix_VTX;
uniform mat4 LeftEyeProjMatrix_VTX;
uniform mat4 RightEyeProjMatrix_VTX;

void main() {
	vec4 worldPosition = u_ModelMatrix * vec4(Position, 1.0);
	v_WorldPos = worldPosition.xyz;
	v_LocalPos = Position;

	gl_Position = LeftEyeProjMatrix_VTX * LeftEyeViewMatrix_VTX * worldPosition;
	gl_SecondaryPositionNV = RightEyeProjMatrix_VTX * RightEyeViewMatrix_VTX * worldPosition;
	gl_Layer = 0;

	mat3 normalMatrix = mat3(transpose(inverse(u_ModelMatrix)));
	v_WorldNormal = normalize(normalMatrix * Normal);
	v_WorldTangent = normalize(normalMatrix * Tangent.xyz);
	v_TangentSign = Tangent.w;

	v_Uv01 = vec4(TexCoord0, TexCoord1);
	v_Uv23 = vec4(TexCoord2, TexCoord3);
	v_VertexColor = Color0;
	v_ViewDir = normalize(LeftEyeInverseViewMatrix_VTX[3].xyz - worldPosition.xyz);
	FragViewIndex = 0.0;
}