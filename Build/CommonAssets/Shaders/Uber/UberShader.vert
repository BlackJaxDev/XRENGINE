// =============================================================================
// Uber Shader - Vertex Stage (single-view / mono)
// =============================================================================
// Companion to UberShader.frag. This is the plain monoscopic build used for
// the desktop editor viewport, mirror cameras, shadow caster passes, and any
// render target that draws a single view at a time.
//
// Responsibilities:
//   * Pick position/normal/tangent from either vertex attributes or a
//     compute-skinned SSBO (skeletal animation is done in a compute pass and
//     the results are streamed in here).
//   * Emit clip-space position via the engine's combined MVP matrix.
//   * Transform the normal basis into world space for the fragment stage.
//   * Forward UVs, vertex color, and a world-space view direction.
//
// See UberShader_OVR.vert / UberShader_NV.vert for the stereo variants that
// produce two eye views from a single draw call.
// =============================================================================

#version 450 core

// Explicitly redeclare gl_PerVertex. Required for separable program objects
// (SPIR-V / Vulkan-style) and keeps the interface block matching downstream.
out gl_PerVertex {
    vec4 gl_Position;
    float gl_PointSize;
    float gl_ClipDistance[];
};

// ============================================
// Vertex Inputs
// ============================================
// Attribute locations are engine-wide conventions — the mesh renderer binds
// vertex buffers to these exact slots regardless of which shader is running.
layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec4 Tangent;     // xyz: tangent, w: bitangent sign (+/-1)
layout(location = 3) in vec2 TexCoord0;
layout(location = 4) in vec2 TexCoord1;
layout(location = 5) in vec2 TexCoord2;
layout(location = 6) in vec2 TexCoord3;
layout(location = 7) in vec4 Color0;

// ============================================
// Vertex Outputs
// ============================================
// UVs are packed two-per-vec4 to conserve interpolator slots. World-space
// position/normal/tangent are the standard inputs the fragment stage expects.
// Locations here are a shared contract with UberShader.frag — do not renumber
// without updating the fragment side too.
layout(location = 0)  out vec4 v_Uv01;          // xy: uv0, zw: uv1
layout(location = 1)  out vec4 v_Uv23;          // xy: uv2, zw: uv3
layout(location = 2)  out vec3 v_WorldPos;      // world-space position
layout(location = 3)  out vec3 v_WorldNormal;   // world-space normal
layout(location = 4)  out vec3 v_WorldTangent;  // world-space tangent
layout(location = 5)  out float v_TangentSign;  // bitangent sign carried from Tangent.w
layout(location = 6)  out vec4 v_VertexColor;
layout(location = 7)  out vec3 v_LocalPos;      // object/local-space position
layout(location = 8)  out vec3 v_ViewDir;       // world-space fragment -> camera
layout(location = 22) out float FragViewIndex;  // which eye (0/1) for stereo

// ============================================
// Uniforms
// ============================================
// Engine-wide uniform block: model/view/projection matrices, camera position,
// lighting arrays, time, etc.
#include "uniforms.glsl"

// ============================================
// Compute-skinning SSBO overrides
// ============================================
// When the mesh has skeletal animation, the engine runs a compute pass that
// writes world-ready (but still object-space) positions/normals/tangents to
// these buffers, indexed by gl_VertexID. The vertex shader then reads from
// them instead of the bound vertex attributes.
//
// Bindings 11 / 12 / 15 must match the compute kernel that produces them.
#ifdef XRENGINE_COMPUTE_SKINNING
layout(std430, binding = 11) buffer SkinnedPositionsInput { vec4 SkinnedPositions[]; };
layout(std430, binding = 12) buffer SkinnedNormalsInput   { vec4 SkinnedNormals[];   };
layout(std430, binding = 15) buffer SkinnedTangentsInput  { vec4 SkinnedTangents[];  };
#endif

void main() {
    // ---- Pick attribute source ---------------------------------------------
    // Either straight from vertex buffers, or from the compute-skinned SSBOs.
    // Tangent.w (the bitangent handedness) is never skinned — it is a pure
    // sign flag authored at mesh build time, so we keep the original here.
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

    // ---- Position: object -> world, object -> clip -------------------------
    vec4 worldPosition = u_ModelMatrix * vec4(pos, 1.0);
    v_WorldPos = worldPosition.xyz;
    v_LocalPos = pos;

    // Clip-space position via the engine-provided combined MVP. Using the
    // combined matrix (instead of P*V*M here) lets the engine fold in any
    // per-camera jitter/offset without touching this shader.
    gl_Position = u_ModelViewProjectionMatrix * vec4(pos, 1.0);

    // ---- Normal / Tangent basis into world space ---------------------------
    // u_NormalMatrix (adjoint of model) correctly handles non-uniform scale
    // for direction vectors; the normalize() below absorbs the determinant
    // scalar so this is exact vs. inverse-transpose for rendering purposes.
    // ~9 muls + 6 subs instead of a full mat3 or mat4 inverse.
    mat3 normalMatrix = u_NormalMatrix;
    v_WorldNormal  = normalize(normalMatrix * norm);
    v_WorldTangent = normalize(normalMatrix * tan);
    v_TangentSign  = tanSign;

    // ---- Pass-throughs -----------------------------------------------------
    // Pack two UV channels per vec4 to save interpolator slots.
    v_Uv01 = vec4(TexCoord0, TexCoord1);
    v_Uv23 = vec4(TexCoord2, TexCoord3);
    v_VertexColor = Color0;

    // View direction in world space (points from the fragment toward the
    // camera). Used by matcap, rim light, parallax, specular, etc.
    v_ViewDir = normalize(u_CameraPosition - worldPosition.xyz);

    // Single-view build — always eye 0.
    FragViewIndex = 0.0;
}
