// =============================================================================
// Uber Shader - Vertex Stage (NVIDIA Single-Pass Stereo)
// =============================================================================
// Stereo variant that uses NVIDIA's single-pass stereo rendering extensions
// (GL_NV_stereo_view_rendering + GL_NV_viewport_array2). Unlike OVR_multiview,
// this runs main() exactly *once* per vertex and outputs two clip-space
// positions: the standard gl_Position for the left eye and
// gl_SecondaryPositionNV for the right. The GPU automatically dispatches
// each triangle to two viewports / layers.
//
// Trade-off vs. OVR_multiview:
//   + Cheaper vertex work (one shader invocation instead of two).
//   + Works on NVIDIA hardware with a simple secondary-position output.
//   - Only one set of varyings is interpolated, so per-eye fragment data
//     must be computed in the fragment stage (we just flag eye 0 here).
//
// This path is typically selected on desktop/PCVR NVIDIA GPUs when
// OVR_multiview is unavailable or slower.
// =============================================================================

#version 450 core
#extension GL_NV_viewport_array2 : require
#extension GL_NV_stereo_view_rendering : require

// Separable-program interface block declaration.
out gl_PerVertex {
	vec4 gl_Position;
	float gl_PointSize;
	float gl_ClipDistance[];
};

// Tell the driver the right eye's layer = left eye's layer + 1. Combined with
// gl_Layer = 0 below this sends the left eye to layer 0 and the right eye to
// layer 1 of a stereo FBO.
layout(secondary_view_offset = 1) out highp int gl_Layer;

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

// Per-eye view and projection matrices. Unlike the OVR path, we need the
// forward view matrices too because we don't re-invert — both eyes' clip
// positions are computed directly in a single pass.
//
// The InverseView matrices are still provided for convenience (used to get
// the camera origin for the view direction without another matrix inverse).
uniform mat4 LeftEyeViewMatrix_VTX;
uniform mat4 RightEyeViewMatrix_VTX;
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

	// Object -> world (shared by both eyes).
	vec4 worldPosition = u_ModelMatrix * vec4(pos, 1.0);
	FragPos = worldPosition.xyz;
	FragPosLocal = pos;

	// Emit both eye clip positions from a single vertex invocation. The NV
	// stereo extension then routes each eye to its own viewport/layer.
	gl_Position            = LeftEyeProjMatrix_VTX  * LeftEyeViewMatrix_VTX  * worldPosition;
	gl_SecondaryPositionNV = RightEyeProjMatrix_VTX * RightEyeViewMatrix_VTX * worldPosition;
	// Left eye = layer 0; secondary_view_offset = 1 places right eye on layer 1.
	gl_Layer = 0;

	// u_NormalMatrix (adjoint of model) handles non-uniform scale for direction
	// vectors; normalize() below absorbs the determinant scalar. Avoids a full
	// mat3/mat4 inverse — see uniforms.glsl for the math.
	mat3 normalMatrix = u_NormalMatrix;
	FragNorm = normalize(normalMatrix * norm);
	FragTan = normalize(normalMatrix * tan);
	FragBinorm = normalize(normalMatrix * (cross(norm, tan) * tanSign));

	FragUV0 = TexCoord0;
	FragColor0 = Color0;
	// FragViewIndex is not per-eye in this single-pass path (we only run
	// main() once), so leave it at 0; the fragment stage can derive the eye
	// from gl_Layer if it cares.
	FragViewIndex = 0.0;
}
