// Engine-authored compatibility include for translated Unity programs.
// It maps Unity built-in shader variables to the uniforms that the engine sets
// (EEngineUniform). Camera uniforms carry the "_VTX" suffix in the vertex stage.
//
// Matrix convention: the frontend compiles with the row-major layout option, so
// a GLSL column becomes an HLSL row. The engine uploads column-major matrices for
// column vectors. transpose() restores the matrix that Unity code expects for
// mul(M, v). The cross-compiled GLSL then reads "M * v".
#ifndef UNITY_SHADER_VARIABLES_INCLUDED
#define UNITY_SHADER_VARIABLES_INCLUDED

// HLSLPROGRAM blocks get no implicit includes, so the Unity includes that need the platform macros include them.
#include "HLSLSupport.cginc"

uniform float4x4 ModelMatrix;
uniform float3 CameraPosition;
uniform float RenderTime;
uniform float DeltaTime;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform float CameraNearZ;
uniform float CameraFarZ;

#if defined(SHADER_STAGE_VERTEX) || defined(SHADER_STAGE_HULL) || defined(SHADER_STAGE_DOMAIN) || defined(SHADER_STAGE_GEOMETRY)
uniform float4x4 ViewMatrix_VTX;
uniform float4x4 InverseViewMatrix_VTX;
uniform float4x4 ProjMatrix_VTX;
uniform float4x4 InverseProjMatrix_VTX;
uniform float4x4 ViewProjectionMatrix_VTX;
#define XR_ENGINE_VIEW ViewMatrix_VTX
#define XR_ENGINE_INVERSE_VIEW InverseViewMatrix_VTX
#define XR_ENGINE_PROJ ProjMatrix_VTX
#define XR_ENGINE_INVERSE_PROJ InverseProjMatrix_VTX
#define XR_ENGINE_VIEW_PROJ ViewProjectionMatrix_VTX
#else
uniform float4x4 ViewMatrix;
uniform float4x4 InverseViewMatrix;
uniform float4x4 ProjMatrix;
uniform float4x4 InverseProjMatrix;
uniform float4x4 ViewProjectionMatrix;
#define XR_ENGINE_VIEW ViewMatrix
#define XR_ENGINE_INVERSE_VIEW InverseViewMatrix
#define XR_ENGINE_PROJ ProjMatrix
#define XR_ENGINE_INVERSE_PROJ InverseProjMatrix
#define XR_ENGINE_VIEW_PROJ ViewProjectionMatrix
#endif

// Returns the inverse of an affine or projective 4x4 matrix with the cofactor method.
float4x4 XR_Inverse(float4x4 m)
{
    float a00 = m[0][0], a01 = m[0][1], a02 = m[0][2], a03 = m[0][3];
    float a10 = m[1][0], a11 = m[1][1], a12 = m[1][2], a13 = m[1][3];
    float a20 = m[2][0], a21 = m[2][1], a22 = m[2][2], a23 = m[2][3];
    float a30 = m[3][0], a31 = m[3][1], a32 = m[3][2], a33 = m[3][3];
    float b00 = a00 * a11 - a01 * a10;
    float b01 = a00 * a12 - a02 * a10;
    float b02 = a00 * a13 - a03 * a10;
    float b03 = a01 * a12 - a02 * a11;
    float b04 = a01 * a13 - a03 * a11;
    float b05 = a02 * a13 - a03 * a12;
    float b06 = a20 * a31 - a21 * a30;
    float b07 = a20 * a32 - a22 * a30;
    float b08 = a20 * a33 - a23 * a30;
    float b09 = a21 * a32 - a22 * a31;
    float b10 = a21 * a33 - a23 * a31;
    float b11 = a22 * a33 - a23 * a32;
    float determinant = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
    float inverseDeterminant = determinant != 0.0 ? 1.0 / determinant : 0.0;
    return float4x4(
        a11 * b11 - a12 * b10 + a13 * b09, a02 * b10 - a01 * b11 - a03 * b09, a31 * b05 - a32 * b04 + a33 * b03, a22 * b04 - a21 * b05 - a23 * b03,
        a12 * b08 - a10 * b11 - a13 * b07, a00 * b11 - a02 * b08 + a03 * b07, a32 * b02 - a30 * b05 - a33 * b01, a20 * b05 - a22 * b02 + a23 * b01,
        a10 * b10 - a11 * b08 + a13 * b06, a01 * b08 - a00 * b10 - a03 * b06, a30 * b04 - a31 * b02 + a33 * b00, a21 * b02 - a20 * b04 - a23 * b00,
        a11 * b07 - a10 * b09 - a12 * b06, a00 * b09 - a01 * b07 + a02 * b06, a31 * b01 - a30 * b03 - a32 * b00, a20 * b03 - a21 * b01 + a22 * b00) * inverseDeterminant;
}

// Transforms.
#define unity_ObjectToWorld transpose(ModelMatrix)
#define unity_WorldToObject XR_Inverse(unity_ObjectToWorld)
#define unity_MatrixV transpose(XR_ENGINE_VIEW)
#define unity_MatrixInvV transpose(XR_ENGINE_INVERSE_VIEW)
#define unity_MatrixVP transpose(XR_ENGINE_VIEW_PROJ)
#define glstate_matrix_projection transpose(XR_ENGINE_PROJ)
#define unity_CameraProjection glstate_matrix_projection
#define unity_CameraInvProjection transpose(XR_ENGINE_INVERSE_PROJ)
#define UNITY_MATRIX_M unity_ObjectToWorld
#define UNITY_MATRIX_I_M unity_WorldToObject
#define UNITY_MATRIX_V unity_MatrixV
#define UNITY_MATRIX_I_V unity_MatrixInvV
#define UNITY_MATRIX_P glstate_matrix_projection
#define UNITY_MATRIX_VP unity_MatrixVP
#define UNITY_MATRIX_MV mul(UNITY_MATRIX_V, UNITY_MATRIX_M)
#define UNITY_MATRIX_T_MV transpose(UNITY_MATRIX_MV)
#define UNITY_MATRIX_IT_MV transpose(mul(UNITY_MATRIX_I_M, UNITY_MATRIX_I_V))
#define UNITY_MATRIX_MVP mul(UNITY_MATRIX_VP, UNITY_MATRIX_M)

// Unity camera space looks along +Z. Engine view space looks along -Z, so the Z axis changes sign.
#define XR_FLIP_Z float4x4(1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, -1.0, 0.0, 0.0, 0.0, 0.0, 1.0)
#define unity_CameraToWorld mul(UNITY_MATRIX_I_V, XR_FLIP_Z)
#define unity_WorldToCamera mul(XR_FLIP_Z, UNITY_MATRIX_V)

// Fixed-function texture matrices. The engine has none, so they are identity.
#define XR_IDENTITY_4X4 float4x4(1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0)
#define UNITY_MATRIX_TEXTURE0 XR_IDENTITY_4X4
#define UNITY_MATRIX_TEXTURE1 XR_IDENTITY_4X4
#define UNITY_MATRIX_TEXTURE2 XR_IDENTITY_4X4
#define UNITY_MATRIX_TEXTURE3 XR_IDENTITY_4X4

// Camera and screen.
// Unity shaders can write to these globals, because Unity compiles in a legacy mode that allows it.
// Static copies keep that legal. Each invocation starts from the engine value.
static float3 _WorldSpaceCameraPos = CameraPosition;
static float4 _ProjectionParams = float4(1.0, CameraNearZ, CameraFarZ, 1.0 / CameraFarZ);
static float4 _ScreenParams = float4(ScreenWidth, ScreenHeight, 1.0 + 1.0 / ScreenWidth, 1.0 + 1.0 / ScreenHeight);
static float4 _ZBufferParams = float4(1.0 - CameraFarZ / CameraNearZ, CameraFarZ / CameraNearZ, (1.0 - CameraFarZ / CameraNearZ) / CameraFarZ, (CameraFarZ / CameraNearZ) / CameraFarZ);
#define unity_OrthoParams float4(0.0, 0.0, 0.0, 0.0)
#define unity_CameraWorldClipPlanes XR_Unsupported_unity_CameraWorldClipPlanes
#define unity_StereoEyeIndex 0
#define unity_LODFade float4(1.0, 1.0, 0.0, 0.0)
#define unity_WorldTransformParams float4(0.0, 0.0, 0.0, determinant((float3x3)unity_ObjectToWorld) < 0.0 ? -1.0 : 1.0)
#define unity_RenderingLayer float4(0.0, 0.0, 0.0, 0.0)

// Time. Unity packs t/20, t, 2t, 3t and sine and cosine variants.
static float4 _Time = float4(RenderTime / 20.0, RenderTime, RenderTime * 2.0, RenderTime * 3.0);
static float4 _SinTime = float4(sin(RenderTime / 8.0), sin(RenderTime / 4.0), sin(RenderTime / 2.0), sin(RenderTime));
static float4 _CosTime = float4(cos(RenderTime / 8.0), cos(RenderTime / 4.0), cos(RenderTime / 2.0), cos(RenderTime));
static float4 unity_DeltaTime = float4(DeltaTime, 1.0 / max(DeltaTime, 1.0e-6), DeltaTime, 1.0 / max(DeltaTime, 1.0e-6));

// Ambient. The engine supplies one ambient color; spherical harmonics use it as the constant band.
uniform float3 GlobalAmbient;
#define unity_SHAr float4(0.0, 0.0, 0.0, GlobalAmbient.r)
#define unity_SHAg float4(0.0, 0.0, 0.0, GlobalAmbient.g)
#define unity_SHAb float4(0.0, 0.0, 0.0, GlobalAmbient.b)
#define unity_SHBr float4(0.0, 0.0, 0.0, 0.0)
#define unity_SHBg float4(0.0, 0.0, 0.0, 0.0)
#define unity_SHBb float4(0.0, 0.0, 0.0, 0.0)
#define unity_SHC float4(0.0, 0.0, 0.0, 0.0)
#define unity_AmbientSky float4(GlobalAmbient, 1.0)
#define unity_AmbientEquator float4(GlobalAmbient, 1.0)
#define unity_AmbientGround float4(GlobalAmbient, 1.0)
#define UNITY_LIGHTMODEL_AMBIENT float4(GlobalAmbient, 1.0)
#define unity_IndirectSpecColor float4(GlobalAmbient, 1.0)
#define unity_FogColor float4(0.0, 0.0, 0.0, 0.0)
#define unity_FogParams float4(0.0, 0.0, 0.0, 0.0)

// Main light. The engine forward light buffer supplies the first directional light.
struct XRBaseLight
{
    float3 Color;
    float DiffuseIntensity;
    float AmbientIntensity;
    float4x4 WorldToLightSpaceProjMatrix;
};

struct XRDirLight
{
    XRBaseLight Base;
    float3 Direction;
    float4x4 WorldToLightInvViewMatrix;
    float4x4 WorldToLightProjMatrix;
    float4x4 WorldToLightSpaceMatrix;
    int CascadeCount;
};

[[vk::binding(22, 3)]] StructuredBuffer<XRDirLight> DirectionalLights;
uniform int DirLightCount;

float4 XR_MainLightPosition()
{
    if (DirLightCount <= 0)
        return float4(0.0, 1.0, 0.0, 0.0);
    return float4(normalize(-DirectionalLights[0].Direction), 0.0);
}

float4 XR_MainLightColor()
{
    if (DirLightCount <= 0)
        return float4(0.0, 0.0, 0.0, 0.0);
    XRBaseLight light = DirectionalLights[0].Base;
    return float4(light.Color * light.DiffuseIntensity, 1.0);
}

#define _WorldSpaceLightPos0 XR_MainLightPosition()
#define _LightPositionRange float4(0.0, 0.0, 0.0, 0.0)
#define _LightProjectionParams float4(0.0, 0.0, 0.0, 0.0)
#define _LightShadowData float4(0.0, 0.0, 0.0, 0.0)
#define unity_4LightPosX0 float4(0.0, 0.0, 0.0, 0.0)
#define unity_4LightPosY0 float4(0.0, 0.0, 0.0, 0.0)
#define unity_4LightPosZ0 float4(0.0, 0.0, 0.0, 0.0)
#define unity_4LightAtten0 float4(0.0, 0.0, 0.0, 0.0)
// Vertex lights are not supplied. The colors are zero, so Shade4PointLights adds nothing.
static float4 unity_LightColor[8];
#define unity_OcclusionMaskSelector float4(1.0, 0.0, 0.0, 0.0)
#define unity_ProbesOcclusion float4(1.0, 1.0, 1.0, 1.0)

// Lightmaps. Translated passes have no lightmap, so the scale and offset are identity.
#define unity_LightmapST float4(1.0, 1.0, 0.0, 0.0)
#define unity_DynamicLightmapST float4(1.0, 1.0, 0.0, 0.0)

// Reflection probe. The binding mapper binds the engine environment cubemap.
TextureCube unity_SpecCube0;
SamplerState samplerunity_SpecCube0;
#define unity_SpecCube1 unity_SpecCube0
#define samplerunity_SpecCube1 samplerunity_SpecCube0
#define unity_SpecCube0_HDR float4(1.0, 1.0, 0.0, 0.0)
#define unity_SpecCube1_HDR float4(1.0, 1.0, 0.0, 0.0)
#define unity_SpecCube0_BoxMax float4(0.0, 0.0, 0.0, 0.0)
#define unity_SpecCube0_BoxMin float4(0.0, 0.0, 0.0, 1.0)
#define unity_SpecCube0_ProbePosition float4(0.0, 0.0, 0.0, 0.0)
#define unity_SpecCube1_BoxMax float4(0.0, 0.0, 0.0, 0.0)
#define unity_SpecCube1_BoxMin float4(0.0, 0.0, 0.0, 1.0)
#define unity_SpecCube1_ProbePosition float4(0.0, 0.0, 0.0, 0.0)

// VRChat globals (_VRChatMirrorMode and others) are not defined here. Shaders declare
// them as plain uniforms, which keep their zero default outside VRChat.

// Unity programs can call UnityObjectToClipPos without an include, because the implicit includes supply it.
#include "UnityShaderUtilities.cginc"

#endif // UNITY_SHADER_VARIABLES_INCLUDED
