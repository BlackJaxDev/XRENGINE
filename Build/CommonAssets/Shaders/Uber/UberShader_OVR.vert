// =============================================================================
// Uber Shader - Vertex Stage (OVR_multiview2 stereo)
// =============================================================================
// Stereo variant of UberShader.vert that uses the GL_OVR_multiview2 extension
// to render both eyes from a single draw call. Each invocation of main()
// gets a distinct value of gl_ViewID_OVR (0 = left, 1 = right), which is used
// to pick the per-eye view and projection matrices.
//
// This is the preferred stereo path on most XR-capable GL drivers (Quest,
// PCVR via Oculus / Monado / WMR). Compared to two separate draws it roughly
// halves CPU submission cost and lets vertex work run twice per vertex
// instead of being fully duplicated.
//
// Mirrors the outputs of UberShader.vert so the same fragment stage works
// with either.
// =============================================================================

#version 450 core
#extension GL_OVR_multiview2 : require

// Re-declared for separable programs, same as mono path.
out gl_PerVertex {
	vec4 gl_Position;
	float gl_PointSize;
	float gl_ClipDistance[];
};

// Request two views (one per eye). The driver invokes the vertex shader
// once per view per vertex, with gl_ViewID_OVR telling us which eye.
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
layout(location = 0) out vec3 FragPos;
layout(location = 1) out vec3 FragNorm;
layout(location = 2) out vec3 FragTan;
layout(location = 3) out vec3 FragBinorm;
layout(location = 4) out vec2 FragUV0;
layout(location = 12) out vec4 FragColor0;
layout(location = 20) out vec3 FragPosLocal;
layout(location = 22) out float FragViewIndex;

// ============================================
// Uniforms
// ============================================
#include "uniforms.glsl"

// Per-eye matrices pushed by the engine each frame. We take inverse-view
// (camera->world) for the view direction, and project with the per-eye
// projection. Using *inverse view* here avoids having to invert mat4s on the
// GPU for every vertex.
uniform mat4 LeftEyeInverseViewMatrix_VTX;
uniform mat4 RightEyeInverseViewMatrix_VTX;
uniform mat4 LeftEyeProjMatrix_VTX;
uniform mat4 RightEyeProjMatrix_VTX;

// ============================================
// Compute-skinning SSBO overrides
// ============================================
#ifdef XRENGINE_COMPUTE_SKINNING
layout(std430, binding = 11) buffer SkinnedPositionsInput { vec4 SkinnedPositions[]; };
layout(std430, binding = 12) buffer SkinnedNormalsInput   { vec4 SkinnedNormals[];   };
layout(std430, binding = 15) buffer SkinnedTangentsInput  { vec4 SkinnedTangents[];  };
#endif

void main() {
#ifdef XRENGINE_COMPUTE_SKINNING
	vec3 pos  = SkinnedPositions[gl_VertexID].xyz;
	vec3 norm = SkinnedNormals[gl_VertexID].xyz;
	vec3 tan  = SkinnedTangents[gl_VertexID].xyz;
#else
	vec3 pos  = Position;
	vec3 norm = Normal;
	vec3 tan  = Tangent.xyz;
#endif
	float tanSign = Tangent.w;

	// Object -> world. World-space data is shared between both eyes so we
	// only have to do this once per vertex-invocation even though each eye
	// runs main() independently.
	vec4 worldPosition = u_ModelMatrix * vec4(pos, 1.0);
	FragPos = worldPosition.xyz;
	FragPosLocal = pos;

	// Select per-eye matrices based on which view we're in.
	bool leftEye = gl_ViewID_OVR == 0;
	mat4 inverseView = leftEye ? LeftEyeInverseViewMatrix_VTX : RightEyeInverseViewMatrix_VTX;
	mat4 projection  = leftEye ? LeftEyeProjMatrix_VTX        : RightEyeProjMatrix_VTX;
	// inverse(inverseView) == view; kept explicit so the engine can pass only
	// the inverse view and we recover the forward matrix here when needed.
	mat4 view = inverse(inverseView);
	gl_Position = projection * view * worldPosition;

	// u_NormalMatrix (adjoint of model) handles non-uniform scale for direction
	// vectors; normalize() below absorbs the determinant scalar. Avoids a full
	// mat3/mat4 inverse — see uniforms.glsl for the math.
	mat3 normalMatrix = u_NormalMatrix;
	FragNorm = normalize(normalMatrix * norm);
	FragTan = normalize(normalMatrix * tan);
	FragBinorm = normalize(normalMatrix * (cross(norm, tan) * tanSign));

	FragUV0 = TexCoord0;
	FragColor0 = Color0;
	// Let the fragment stage know which eye is being shaded — useful for
	// per-eye effects (e.g. mask textures, UI overlays, stereo debug).
	FragViewIndex = float(gl_ViewID_OVR);
}
