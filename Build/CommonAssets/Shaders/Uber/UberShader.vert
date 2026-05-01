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
// Locations here are a shared contract with UberShader.frag and the engine's
// generated vertex shader.
layout(location = 0)  out vec3 FragPos;        // world-space position
layout(location = 1)  out vec3 FragNorm;       // world-space geometric normal
layout(location = 2)  out vec3 FragTan;        // world-space tangent
layout(location = 3)  out vec3 FragBinorm;     // world-space bitangent
layout(location = 4)  out vec2 FragUV0;        // primary texture coordinates
layout(location = 12) out vec4 FragColor0;     // per-vertex RGBA color
layout(location = 20) out vec3 FragPosLocal;   // object/local-space position
layout(location = 22) out float FragViewIndex; // which eye (0/1) for stereo

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
    FragPos = worldPosition.xyz;
    FragPosLocal = pos;

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
    FragNorm = normalize(normalMatrix * norm);
    FragTan = normalize(normalMatrix * tan);
    FragBinorm = normalize(normalMatrix * (cross(norm, tan) * tanSign));

    // ---- Pass-throughs -----------------------------------------------------
    FragUV0 = TexCoord0;
    FragColor0 = Color0;

    // Single-view build — always eye 0.
    FragViewIndex = 0.0;
}
