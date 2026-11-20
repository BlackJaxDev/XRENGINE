// Engine-authored compatibility include for translated Unity programs.
// It gives the public types and macros of Unity's HLSLSupport.cginc for the
// Slang frontend. It does not contain Unity source. Platform values describe an
// OpenGL-style target: clip depth -1..1, UV origin at the bottom, no reversed Z.
// The engine converts the result for Vulkan.
#ifndef HLSL_SUPPORT_INCLUDED
#define HLSL_SUPPORT_INCLUDED

#define SHADER_API_DESKTOP 1
#define UNITY_COMPILER_HLSL 1
#define UNITY_REVERSED_Z 0
#define UNITY_UV_STARTS_AT_TOP 0
#define UNITY_NEAR_CLIP_VALUE (-1.0)
#define UNITY_RAW_FAR_CLIP_VALUE (1.0)
// Clip depth to a 0..far range, for fog. With OpenGL clip depth and no reversed Z, Unity uses the value as it is.
#define UNITY_Z_0_FAR_FROM_CLIPSPACE(coord) (coord)
#define UNITY_CAN_COMPILE_TESSELLATION 1
#define UNITY_ATTEN_CHANNEL r
#define UNITY_NO_DXT5nm 0
#define UNITY_SUPPORT_DEPTH_FETCH 1
#define UNITY_INITIALIZE_OUTPUT(type, name) name = (type)0
#define UNITY_PROJ_COORD(a) a
#define UNITY_BRANCH [branch]
#define UNITY_FLATTEN [flatten]
#define UNITY_UNROLL [unroll]
#define UNITY_UNROLLX(n) [unroll(n)]
#define UNITY_LOOP [loop]
#define UNITY_FASTOPT
// Unity writes the hull and domain attributes through these macros. Without them, Slang ignores the
// attributes, and the hull stage has no control point count, no patch constant function, and scalar outputs.
#define UNITY_domain domain
#define UNITY_outputcontrolpoints outputcontrolpoints
#define UNITY_outputtopology outputtopology
#define UNITY_partitioning partitioning
#define UNITY_patchconstantfunc patchconstantfunc
#define UNITY_POSITION(pos) float4 pos : SV_POSITION
#define UNITY_VPOS_TYPE float4
#define VPOS SV_POSITION
#define VFACE SV_IsFrontFace
#define UNITY_SHADER_NO_UPGRADE 1
#define CBUFFER_START(name)
#define CBUFFER_END
#define UNITY_DECLARE_SHADOWMAP(tex) Texture2D tex; SamplerComparisonState sampler##tex
#define UNITY_SAMPLE_SHADOW(tex, coord) tex.SampleCmpLevelZero(sampler##tex, (coord).xy, (coord).z)
#define UNITY_SAMPLE_SHADOW_PROJ(tex, coord) tex.SampleCmpLevelZero(sampler##tex, (coord).xy / (coord).w, (coord).z / (coord).w)

// Low-precision Unity types use full precision. The engine targets desktop GPUs.
#define fixed float
#define fixed2 float2
#define fixed3 float3
#define fixed4 float4
#define fixed2x2 float2x2
#define fixed3x3 float3x3
#define fixed4x4 float4x4
#define half float
#define half2 float2
#define half3 float3
#define half4 float4
#define half2x2 float2x2
#define half3x3 float3x3
#define half4x4 float4x4
#define min16float float
#define min16float2 float2
#define min16float3 float3
#define min16float4 float4
#define min10float float
#define min10float2 float2
#define min10float3 float3
#define min10float4 float4

// Legacy Cg samplers are combined texture-sampler objects.
#define sampler2D Sampler2D
#define sampler2D_float Sampler2D
#define sampler2D_half Sampler2D
#define sampler3D Sampler3D
#define samplerCUBE SamplerCube
#define samplerCUBE_float SamplerCube
#define samplerCUBE_half SamplerCube

// Precision-named texture types. Direct3D 11 uses full-precision textures for all of them.
#define Texture2D_float Texture2D
#define Texture2D_half Texture2D
#define Texture3D_float Texture3D
#define Texture3D_half Texture3D
#define TextureCube_float TextureCube
#define TextureCube_half TextureCube
#define Texture2DArray_float Texture2DArray
#define Texture2DArray_half Texture2DArray

float4 tex2D(Sampler2D s, float2 uv) { return s.Sample(uv); }
float4 tex2D(Sampler2D s, float2 uv, float2 ddxUv, float2 ddyUv) { return s.SampleGrad(uv, ddxUv, ddyUv); }
float4 tex2D(Sampler2D s, float3 uvCompare) { return s.Sample(uvCompare.xy); }
float4 tex2D(Sampler2D s, float4 uv) { return s.Sample(uv.xy); }
float4 tex2Dlod(Sampler2D s, float4 uvLod) { return s.SampleLevel(uvLod.xy, uvLod.w); }
float4 tex2Dbias(Sampler2D s, float4 uvBias) { return s.SampleBias(uvBias.xy, uvBias.w); }
float4 tex2Dgrad(Sampler2D s, float2 uv, float2 ddxUv, float2 ddyUv) { return s.SampleGrad(uv, ddxUv, ddyUv); }
float4 tex2Dproj(Sampler2D s, float4 uvProj) { return s.Sample(uvProj.xy / uvProj.w); }
float4 tex2Dproj(Sampler2D s, float3 uvProj) { return s.Sample(uvProj.xy / uvProj.z); }
float4 tex3D(Sampler3D s, float3 uvw) { return s.Sample(uvw); }
float4 tex3Dlod(Sampler3D s, float4 uvwLod) { return s.SampleLevel(uvwLod.xyz, uvwLod.w); }
float4 texCUBE(SamplerCube s, float3 direction) { return s.Sample(direction); }
float4 texCUBElod(SamplerCube s, float4 directionLod) { return s.SampleLevel(directionLod.xyz, directionLod.w); }
float4 texCUBEbias(SamplerCube s, float4 directionBias) { return s.SampleBias(directionBias.xyz, directionBias.w); }

// Texture and sampler declaration macros (Direct3D 11 style: separate objects).
#define UNITY_DECLARE_TEX2D(tex) Texture2D tex; SamplerState sampler##tex
#define UNITY_DECLARE_TEX2D_NOSAMPLER(tex) Texture2D tex
#define UNITY_DECLARE_TEX2D_FLOAT(tex) Texture2D<float4> tex; SamplerState sampler##tex
#define UNITY_DECLARE_TEX2D_HALF(tex) Texture2D<float4> tex; SamplerState sampler##tex
#define UNITY_DECLARE_TEX2D_NOSAMPLER_INT(tex) Texture2D<int4> tex
#define UNITY_DECLARE_TEX2D_NOSAMPLER_UINT(tex) Texture2D<uint4> tex
#define UNITY_DECLARE_TEXCUBE(tex) TextureCube tex; SamplerState sampler##tex
#define UNITY_DECLARE_TEXCUBE_NOSAMPLER(tex) TextureCube tex
#define UNITY_DECLARE_TEX3D(tex) Texture3D tex; SamplerState sampler##tex
#define UNITY_DECLARE_TEX3D_NOSAMPLER(tex) Texture3D tex
#define UNITY_DECLARE_TEX2DARRAY(tex) Texture2DArray tex; SamplerState sampler##tex
#define UNITY_DECLARE_TEX2DARRAY_NOSAMPLER(tex) Texture2DArray tex
#define UNITY_DECLARE_TEXCUBEARRAY(tex) TextureCubeArray tex; SamplerState sampler##tex
#define UNITY_DECLARE_TEXCUBEARRAY_NOSAMPLER(tex) TextureCubeArray tex
#define UNITY_ARGS_TEX2D(tex) Texture2D tex, SamplerState sampler##tex
#define UNITY_PASS_TEX2D(tex) tex, sampler##tex
#define UNITY_ARGS_TEXCUBE(tex) TextureCube tex, SamplerState sampler##tex
#define UNITY_PASS_TEXCUBE(tex) tex, sampler##tex
#define UNITY_PASS_TEXCUBE_SAMPLER(tex, samplertex) tex, sampler##samplertex

#define UNITY_SAMPLE_TEX2D(tex, coord) tex.Sample(sampler##tex, coord)
#define UNITY_SAMPLE_TEX2D_LOD(tex, coord, lod) tex.SampleLevel(sampler##tex, coord, lod)
#define UNITY_SAMPLE_TEX2D_BIAS(tex, coord, bias) tex.SampleBias(sampler##tex, coord, bias)
#define UNITY_SAMPLE_TEX2D_GRAD(tex, coord, dx, dy) tex.SampleGrad(sampler##tex, coord, dx, dy)
#define UNITY_SAMPLE_TEX2D_SAMPLER(tex, samplertex, coord) tex.Sample(sampler##samplertex, coord)
#define UNITY_SAMPLE_TEX2D_SAMPLER_LOD(tex, samplertex, coord, lod) tex.SampleLevel(sampler##samplertex, coord, lod)
#define UNITY_SAMPLE_TEX2D_SAMPLER_BIAS(tex, samplertex, coord, bias) tex.SampleBias(sampler##samplertex, coord, bias)
#define UNITY_SAMPLE_TEX2D_SAMPLER_GRAD(tex, samplertex, coord, dx, dy) tex.SampleGrad(sampler##samplertex, coord, dx, dy)
#define UNITY_SAMPLE_TEXCUBE(tex, coord) tex.Sample(sampler##tex, coord)
#define UNITY_SAMPLE_TEXCUBE_LOD(tex, coord, lod) tex.SampleLevel(sampler##tex, coord, lod)
#define UNITY_SAMPLE_TEXCUBE_SAMPLER(tex, samplertex, coord) tex.Sample(sampler##samplertex, coord)
#define UNITY_SAMPLE_TEXCUBE_SAMPLER_LOD(tex, samplertex, coord, lod) tex.SampleLevel(sampler##samplertex, coord, lod)
#define UNITY_SAMPLE_TEX3D(tex, coord) tex.Sample(sampler##tex, coord)
#define UNITY_SAMPLE_TEX3D_LOD(tex, coord, lod) tex.SampleLevel(sampler##tex, coord, lod)
#define UNITY_SAMPLE_TEX3D_SAMPLER(tex, samplertex, coord) tex.Sample(sampler##samplertex, coord)
#define UNITY_SAMPLE_TEX2DARRAY(tex, coord) tex.Sample(sampler##tex, coord)
#define UNITY_SAMPLE_TEX2DARRAY_LOD(tex, coord, lod) tex.SampleLevel(sampler##tex, coord, lod)
#define UNITY_SAMPLE_TEX2DARRAY_SAMPLER(tex, samplertex, coord) tex.Sample(sampler##samplertex, coord)
#define UNITY_SAMPLE_TEX2DARRAY_SAMPLER_LOD(tex, samplertex, coord, lod) tex.SampleLevel(sampler##samplertex, coord, lod)
#define UNITY_SAMPLE_TEXCUBEARRAY(tex, coord) tex.Sample(sampler##tex, coord)
#define UNITY_SAMPLE_TEXCUBEARRAY_LOD(tex, coord, lod) tex.SampleLevel(sampler##tex, coord, lod)

// Depth and screen-space textures.
#define UNITY_DECLARE_DEPTH_TEXTURE(tex) Sampler2D tex
#define UNITY_DECLARE_DEPTH_TEXTURE_MS(tex) Sampler2D tex
#define SAMPLE_DEPTH_TEXTURE(tex, uv) (tex2D(tex, uv).r)
#define SAMPLE_DEPTH_TEXTURE_PROJ(tex, uv) (tex2Dproj(tex, uv).r)
#define SAMPLE_DEPTH_TEXTURE_LOD(tex, uv) (tex2Dlod(tex, uv).r)
#define SAMPLE_RAW_DEPTH_TEXTURE(tex, uv) (tex2D(tex, uv))
#define SAMPLE_RAW_DEPTH_TEXTURE_PROJ(tex, uv) (tex2Dproj(tex, uv))
#define SAMPLE_RAW_DEPTH_TEXTURE_LOD(tex, uv) (tex2Dlod(tex, uv))
#define UNITY_DECLARE_SCREENSPACE_TEXTURE(tex) Sampler2D tex
#define UNITY_DECLARE_SCREENSPACE_SHADOWMAP(tex) Sampler2D tex
#define UNITY_SAMPLE_SCREENSPACE_TEXTURE(tex, uv) tex2D(tex, uv)
#define UNITY_SAMPLE_SCREEN_SHADOW(tex, uv) tex2Dproj(tex, uv).r
// The engine stores one-channel textures in the red channel.
#define UNITY_SAMPLE_1CHANNEL(x, y) (tex2D(x, y).r)
#define UNITY_DECLARE_FRAMEBUFFER_INPUT_FLOAT(idx) Sampler2D _UnityFBInput##idx
#define UNITY_READ_FRAMEBUFFER_INPUT(idx, pos) tex2D(_UnityFBInput##idx, (pos).xy)

#endif // HLSL_SUPPORT_INCLUDED
