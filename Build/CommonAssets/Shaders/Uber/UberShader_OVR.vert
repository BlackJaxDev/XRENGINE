// Uber Shader - Vertex Shader (OVR Multiview)

#version 450 core
#extension GL_OVR_multiview2 : require

out gl_PerVertex {
	vec4 gl_Position;
	float gl_PointSize;
	float gl_ClipDistance[];
};

layout(num_views = 2) in;

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

// ============================================
// Uniforms
// ============================================
#include "uniforms.glsl"

uniform mat4 LeftEyeInverseViewMatrix_VTX;
uniform mat4 RightEyeInverseViewMatrix_VTX;
uniform mat4 LeftEyeProjMatrix_VTX;
uniform mat4 RightEyeProjMatrix_VTX;

void main() {
	vec4 worldPosition = u_ModelMatrix * vec4(Position, 1.0);
	v_WorldPos = worldPosition.xyz;
	v_LocalPos = Position;

	bool leftEye = gl_ViewID_OVR == 0;
	mat4 inverseView = leftEye ? LeftEyeInverseViewMatrix_VTX : RightEyeInverseViewMatrix_VTX;
	mat4 projection = leftEye ? LeftEyeProjMatrix_VTX : RightEyeProjMatrix_VTX;
	mat4 view = inverse(inverseView);
	gl_Position = projection * view * worldPosition;

	mat3 normalMatrix = mat3(transpose(inverse(u_ModelMatrix)));
	v_WorldNormal = normalize(normalMatrix * Normal);
	v_WorldTangent = normalize(normalMatrix * Tangent.xyz);
	v_TangentSign = Tangent.w;

	v_Uv01 = vec4(TexCoord0, TexCoord1);
	v_Uv23 = vec4(TexCoord2, TexCoord3);
	v_VertexColor = Color0;
	v_ViewDir = normalize(inverseView[3].xyz - worldPosition.xyz);
}