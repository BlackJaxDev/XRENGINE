#version 450

// Volumetric fog scatter pass.
//
// Phase 2: runs at half internal resolution against a pre-downsampled half-res
// depth view (VolumetricFogHalfDepth). The full-res bilateral upscale
// (VolumetricFogUpscale.fs) then reconstructs the per-pixel result using the
// full-res DepthView for edge weighting.
//
//   rgb = in-scattered radiance
//   a   = transmittance (1.0 = no fog, 0.0 = fully occluded)
//
// The composite in PostProcess.fs fetches the upscaled full-res texture
// (VolumetricFogColor) and applies:
//   hdrScene = hdrScene * volumetric.a + volumetric.rgb;
//
// This shader is mono-only; stereo falls back to no scatter until a
// sampler2DArray stereo variant is added.

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

// Half-resolution depth view produced by VolumetricFogHalfDepthDownsample.fs.
// Sampled as the raymarch surface depth for this half-res pixel.
uniform sampler2D VolumetricFogHalfDepth;
uniform sampler2D ShadowMap;
uniform sampler2DArray ShadowMapArray;

uniform vec3 CameraPosition;
uniform mat4 ViewMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform float RenderTime;
uniform int DirLightCount;
uniform int DepthMode;

uniform bool ShadowMapEnabled;
uniform bool UseCascadedDirectionalShadows;
uniform bool EnableCascadedShadows;
uniform float ShadowBiasMin;
uniform float ShadowBiasMax;
uniform int ShadowSamples;
uniform float ShadowFilterRadius;

struct BaseLight
{
  vec3 Color;
  float DiffuseIntensity;
  float AmbientIntensity;
  mat4 WorldToLightSpaceProjMatrix;
};

struct DirLight
{
  BaseLight Base;
  vec3 Direction;
  mat4 WorldToLightInvViewMatrix;
  mat4 WorldToLightProjMatrix;
  mat4 WorldToLightSpaceMatrix;
  float CascadeSplits[8];
  mat4 CascadeMatrices[8];
  int CascadeCount;
};

uniform DirLight DirectionalLights[2];

const int MaxVolumetricFogVolumes = 4;

struct VolumetricFogStruct
{
  bool Enabled;
  float Intensity;
  float MaxDistance;
  float StepSize;
  float JitterStrength;
  int VolumeCount;
};
uniform VolumetricFogStruct VolumetricFog;
uniform mat4 VolumetricFogWorldToLocal[MaxVolumetricFogVolumes];
uniform vec4 VolumetricFogColorDensity[MaxVolumetricFogVolumes];
uniform vec4 VolumetricFogHalfExtentsEdgeFade[MaxVolumetricFogVolumes];
uniform vec4 VolumetricFogNoiseScaleThreshold[MaxVolumetricFogVolumes];
uniform vec4 VolumetricFogNoiseOffsetAmount[MaxVolumetricFogVolumes];
uniform vec4 VolumetricFogNoiseVelocity[MaxVolumetricFogVolumes];
uniform vec4 VolumetricFogLightParams[MaxVolumetricFogVolumes];

float saturate(float value)
{
  return clamp(value, 0.0f, 1.0f);
}
float XRENGINE_ResolveDepth(float depth)
{
  return DepthMode == 1 ? (1.0f - depth) : depth;
}
vec3 XRENGINE_WorldPosFromDepthRaw(float depth, vec2 uv, mat4 invProj, mat4 invView)
{
  vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
  vec4 viewSpacePosition = invProj * clipSpacePosition;
  viewSpacePosition /= viewSpacePosition.w;
  return (invView * viewSpacePosition).xyz;
}
float interleavedGradientNoise(vec2 pixelCoord)
{
  return fract(52.9829189f * fract(dot(pixelCoord, vec2(0.06711056f, 0.00583715f))));
}
float hash13(vec3 p)
{
  p = fract(p * 0.1031f);
  p += dot(p, p.zyx + 31.32f);
  return fract((p.x + p.y) * p.z);
}
float valueNoise(vec3 p)
{
  vec3 cell = floor(p);
  vec3 local = fract(p);
  vec3 smoothLocal = local * local * (3.0f - 2.0f * local);

  float n000 = hash13(cell + vec3(0.0f, 0.0f, 0.0f));
  float n100 = hash13(cell + vec3(1.0f, 0.0f, 0.0f));
  float n010 = hash13(cell + vec3(0.0f, 1.0f, 0.0f));
  float n110 = hash13(cell + vec3(1.0f, 1.0f, 0.0f));
  float n001 = hash13(cell + vec3(0.0f, 0.0f, 1.0f));
  float n101 = hash13(cell + vec3(1.0f, 0.0f, 1.0f));
  float n011 = hash13(cell + vec3(0.0f, 1.0f, 1.0f));
  float n111 = hash13(cell + vec3(1.0f, 1.0f, 1.0f));

  float nx00 = mix(n000, n100, smoothLocal.x);
  float nx10 = mix(n010, n110, smoothLocal.x);
  float nx01 = mix(n001, n101, smoothLocal.x);
  float nx11 = mix(n011, n111, smoothLocal.x);
  float nxy0 = mix(nx00, nx10, smoothLocal.y);
  float nxy1 = mix(nx01, nx11, smoothLocal.y);
  return mix(nxy0, nxy1, smoothLocal.z);
}
float fbm3(vec3 p)
{
  float sum = 0.0f;
  float amplitude = 0.6f;
  float frequency = 1.0f;
  for (int octave = 0; octave < 3; ++octave)
  {
    sum += valueNoise(p * frequency) * amplitude;
    frequency *= 2.02f;
    amplitude *= 0.5f;
  }
  return sum;
}
float PhaseHenyeyGreenstein(float cosTheta, float anisotropy)
{
  const float Pi = 3.14159265359f;
  float g = clamp(anisotropy, -0.95f, 0.95f);
  float g2 = g * g;
  float denom = pow(max(1.0f + g2 - 2.0f * g * cosTheta, 0.001f), 1.5f);
  return (1.0f - g2) / (4.0f * Pi * denom);
}
float ComputeBoxFade(vec3 localPos, vec3 halfExtents, float edgeFade)
{
  vec3 safeExtents = max(halfExtents, vec3(0.0001f));
  vec3 normalized = abs(localPos) / safeExtents;
  float boundary = max(max(normalized.x, normalized.y), normalized.z);
  if (boundary >= 1.0f)
    return 0.0f;

  float fadeWidth = clamp(edgeFade, 0.0001f, 1.0f);
  return 1.0f - smoothstep(1.0f - fadeWidth, 1.0f, boundary);
}
vec3 ProjectShadowCoord(mat4 lightMatrix, vec3 samplePosWS)
{
  vec4 samplePosLightSpace = lightMatrix * vec4(samplePosWS, 1.0f);
  vec3 shadowCoord = samplePosLightSpace.xyz / samplePosLightSpace.w;
  return shadowCoord * 0.5f + 0.5f;
}
bool ShadowCoordInBounds(vec3 shadowCoord)
{
  return shadowCoord.x >= 0.0f && shadowCoord.x <= 1.0f
    && shadowCoord.y >= 0.0f && shadowCoord.y <= 1.0f
    && shadowCoord.z >= 0.0f && shadowCoord.z <= 1.0f;
}
float ViewDepthFromWorldPos(vec3 samplePosWS)
{
  vec4 viewPos = ViewMatrix * vec4(samplePosWS, 1.0f);
  return abs(viewPos.z);
}
int GetPrimaryDirectionalCascadeIndex(vec3 samplePosWS)
{
  if (!UseCascadedDirectionalShadows || !EnableCascadedShadows || DirLightCount <= 0)
    return -1;

  int cascadeCount = min(DirectionalLights[0].CascadeCount, 8);
  if (cascadeCount <= 0)
    return -1;

  float viewDepth = ViewDepthFromWorldPos(samplePosWS);
  for (int i = 0; i < cascadeCount; ++i)
  {
    if (viewDepth <= DirectionalLights[0].CascadeSplits[i])
      return i;
  }
  return cascadeCount - 1;
}
float SampleShadowMapSimple(sampler2D shadowMap, vec3 shadowCoord, float bias)
{
  float depth = texture(shadowMap, shadowCoord.xy).r;
  return (shadowCoord.z - bias) > depth ? 0.0f : 1.0f;
}
float SampleShadowMapTent4(sampler2D shadowMap, vec3 shadowCoord, float bias, float filterRadius)
{
  vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0));
  vec2 radius = max(vec2(max(filterRadius, 0.0f)), texelSize);
  float lit = 0.0f;
  lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(-0.5f, -0.5f) * radius).r ? 1.0f : 0.0f;
  lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2( 0.5f, -0.5f) * radius).r ? 1.0f : 0.0f;
  lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(-0.5f,  0.5f) * radius).r ? 1.0f : 0.0f;
  lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2( 0.5f,  0.5f) * radius).r ? 1.0f : 0.0f;
  return lit * 0.25f;
}
float SampleShadowMapArraySimple(sampler2DArray shadowMap, vec3 shadowCoord, float layer, float bias)
{
  float depth = texture(shadowMap, vec3(shadowCoord.xy, layer)).r;
  return (shadowCoord.z - bias) > depth ? 0.0f : 1.0f;
}
float SampleShadowMapArrayTent4(sampler2DArray shadowMap, vec3 shadowCoord, float layer, float bias, float filterRadius)
{
  vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0).xy);
  vec2 radius = max(vec2(max(filterRadius, 0.0f)), texelSize);
  float lit = 0.0f;
  lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(-0.5f, -0.5f) * radius, layer)).r ? 1.0f : 0.0f;
  lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2( 0.5f, -0.5f) * radius, layer)).r ? 1.0f : 0.0f;
  lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(-0.5f,  0.5f) * radius, layer)).r ? 1.0f : 0.0f;
  lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2( 0.5f,  0.5f) * radius, layer)).r ? 1.0f : 0.0f;
  return lit * 0.25f;
}
float EvaluatePrimaryDirectionalShadow(vec3 samplePosWS)
{
  if (DirLightCount <= 0 || !ShadowMapEnabled)
    return 1.0f;

  float bias = max(ShadowBiasMin, ShadowBiasMax * 0.25f);
  int cascadeIndex = GetPrimaryDirectionalCascadeIndex(samplePosWS);
  if (cascadeIndex >= 0)
  {
    vec3 cascadeCoord = ProjectShadowCoord(DirectionalLights[0].CascadeMatrices[cascadeIndex], samplePosWS);
    if (ShadowCoordInBounds(cascadeCoord))
    {
      float filterRadius = ShadowFilterRadius * (1.0f + float(cascadeIndex) * 0.35f);
      return ShadowSamples <= 1
        ? SampleShadowMapArraySimple(ShadowMapArray, cascadeCoord, float(cascadeIndex), bias)
        : SampleShadowMapArrayTent4(ShadowMapArray, cascadeCoord, float(cascadeIndex), bias, filterRadius);
    }
  }

  vec3 shadowCoord = ProjectShadowCoord(DirectionalLights[0].WorldToLightSpaceMatrix, samplePosWS);
  if (!ShadowCoordInBounds(shadowCoord))
    return 1.0f;

  return ShadowSamples <= 1
    ? SampleShadowMapSimple(ShadowMap, shadowCoord, bias)
    : SampleShadowMapTent4(ShadowMap, shadowCoord, bias, ShadowFilterRadius);
}
float EvaluateVolumeDensity(int index, vec3 samplePosWS)
{
  vec3 localPos = (VolumetricFogWorldToLocal[index] * vec4(samplePosWS, 1.0f)).xyz;
  vec3 halfExtents = VolumetricFogHalfExtentsEdgeFade[index].xyz;
  float edgeMask = ComputeBoxFade(localPos, halfExtents, VolumetricFogHalfExtentsEdgeFade[index].w);
  if (edgeMask <= 0.0f)
    return 0.0f;

  vec4 noiseParams = VolumetricFogNoiseScaleThreshold[index];
  float noiseMask = 1.0f;
  if (noiseParams.x > 0.0f && noiseParams.z > 0.0f)
  {
    vec3 noiseSamplePos = localPos * noiseParams.x
      + VolumetricFogNoiseOffsetAmount[index].xyz
      + VolumetricFogNoiseVelocity[index].xyz * RenderTime;
    float noiseValue = fbm3(noiseSamplePos);
    float thresholdMask = smoothstep(noiseParams.y - 0.15f, noiseParams.y + 0.15f, noiseValue);
    noiseMask = mix(1.0f, thresholdMask, saturate(noiseParams.z));
  }
  return VolumetricFogColorDensity[index].w * edgeMask * noiseMask;
}
vec3 EvaluateVolumeLighting(int index, vec3 viewToCamera, float shadowFactor)
{
  float lightContribution = VolumetricFogLightParams[index].x;
  if (DirLightCount <= 0 || lightContribution <= 0.0f)
    return vec3(0.0f);

  vec3 lightDir = normalize(-DirectionalLights[0].Direction);
  vec3 lightColor = DirectionalLights[0].Base.Color * DirectionalLights[0].Base.DiffuseIntensity;
  float phase = PhaseHenyeyGreenstein(dot(viewToCamera, lightDir), VolumetricFogLightParams[index].y);
  return lightColor * shadowFactor * lightContribution * phase * 4.0f;
}
// Slab test against a single volume OBB in its local space.
bool IntersectVolumeOBB(int index, vec3 rayOriginWS, vec3 rayDirWS, out float tNear, out float tFar)
{
  mat4 worldToLocal = VolumetricFogWorldToLocal[index];
  vec3 localOrigin = (worldToLocal * vec4(rayOriginWS, 1.0f)).xyz;
  vec3 localDir = (worldToLocal * vec4(rayDirWS, 0.0f)).xyz;
  vec3 halfExtents = VolumetricFogHalfExtentsEdgeFade[index].xyz;

  vec3 safeDir = mix(localDir, vec3(1e-5f), lessThan(abs(localDir), vec3(1e-5f)));
  vec3 invDir = 1.0f / safeDir;
  vec3 t0 = (-halfExtents - localOrigin) * invDir;
  vec3 t1 = ( halfExtents - localOrigin) * invDir;
  vec3 tMinV = min(t0, t1);
  vec3 tMaxV = max(t0, t1);
  tNear = max(max(tMinV.x, tMinV.y), tMinV.z);
  tFar  = min(min(tMaxV.x, tMaxV.y), tMaxV.z);
  return tFar >= max(tNear, 0.0f);
}
vec4 ComputeVolumetricFog(vec2 uv)
{
  if (!VolumetricFog.Enabled || VolumetricFog.VolumeCount <= 0 || VolumetricFog.Intensity <= 0.0f || VolumetricFog.MaxDistance <= 0.0f)
    return vec4(0.0f, 0.0f, 0.0f, 1.0f);

  vec2 sourceUv = clamp(uv, vec2(0.0f), vec2(1.0f));
  float rawDepth = texture(VolumetricFogHalfDepth, sourceUv).r;
  float resolvedDepth = XRENGINE_ResolveDepth(rawDepth);

  float rayLength = VolumetricFog.MaxDistance;
  if (resolvedDepth < 0.999999f)
  {
    vec3 surfacePosWS = XRENGINE_WorldPosFromDepthRaw(rawDepth, sourceUv, InverseProjMatrix, InverseViewMatrix);
    rayLength = min(rayLength, distance(CameraPosition, surfacePosWS));
  }

  if (rayLength <= 0.0f)
    return vec4(0.0f, 0.0f, 0.0f, 1.0f);

  vec3 rayDir;
  if (resolvedDepth >= 0.999999f)
  {
    vec4 viewFar = InverseProjMatrix * vec4(sourceUv * 2.0f - 1.0f, 1.0f, 1.0f);
    viewFar /= max(viewFar.w, 0.0001f);
    vec3 worldFar = (InverseViewMatrix * viewFar).xyz;
    rayDir = normalize(worldFar - CameraPosition);
  }
  else
  {
    rayDir = normalize(XRENGINE_WorldPosFromDepthRaw(rawDepth, sourceUv, InverseProjMatrix, InverseViewMatrix) - CameraPosition);
  }

  // Compute the union of all volume OBB intersections along the ray. Pixels
  // whose view ray never enters any volume OBB contribute no scattering or
  // extinction, so we skip the march entirely.
  float unionTNear = rayLength;
  float unionTFar = 0.0f;
  bool anyHit = false;
  for (int volumeIndex = 0; volumeIndex < VolumetricFog.VolumeCount; ++volumeIndex)
  {
    float tNear, tFar;
    if (!IntersectVolumeOBB(volumeIndex, CameraPosition, rayDir, tNear, tFar))
      continue;
    tNear = max(tNear, 0.0f);
    tFar  = min(tFar, rayLength);
    if (tFar <= tNear)
      continue;
    unionTNear = min(unionTNear, tNear);
    unionTFar  = max(unionTFar, tFar);
    anyHit = true;
  }

  if (!anyHit || unionTFar <= unionTNear)
    return vec4(0.0f, 0.0f, 0.0f, 1.0f);

  float stepSize = max(VolumetricFog.StepSize, 0.25f);
  float marchLength = unionTFar - unionTNear;
  int stepCount = max(1, int(ceil(marchLength / stepSize)));

  // Time-varying jitter seed lets TAA / temporal accumulation average the
  // per-pixel noise across frames.
  float jitter = (interleavedGradientNoise(gl_FragCoord.xy + fract(RenderTime * 7.0f) * 64.0f) - 0.5f)
               * stepSize * VolumetricFog.JitterStrength;
  float t = unionTNear + max(0.0f, 0.5f * stepSize + jitter);
  vec3 accumulatedScattering = vec3(0.0f);
  float transmittance = 1.0f;

  for (int stepIndex = 0; stepIndex < stepCount; ++stepIndex)
  {
    if (t >= unionTFar || transmittance <= 0.01f)
      break;

    float currentStep = min(stepSize, unionTFar - t);
    vec3 samplePosWS = CameraPosition + rayDir * (t + currentStep * 0.5f);
    vec3 stepScattering = vec3(0.0f);
    float stepExtinction = 0.0f;
    vec3 viewToCamera = -rayDir;
    float shadowFactor = EvaluatePrimaryDirectionalShadow(samplePosWS);

    for (int volumeIndex = 0; volumeIndex < VolumetricFog.VolumeCount; ++volumeIndex)
    {
      float density = EvaluateVolumeDensity(volumeIndex, samplePosWS) * VolumetricFog.Intensity;
      if (density <= 0.0f)
        continue;

      vec3 albedo = VolumetricFogColorDensity[volumeIndex].rgb;
      vec3 lighting = EvaluateVolumeLighting(volumeIndex, viewToCamera, shadowFactor);
      stepScattering += albedo * lighting * density;
      stepExtinction += density;
    }

    if (stepExtinction > 0.0f)
    {
      accumulatedScattering += stepScattering * currentStep * transmittance;
      transmittance *= exp(-stepExtinction * currentStep);
    }

    t += currentStep;
  }

  return vec4(accumulatedScattering, transmittance);
}

void main()
{
  // FullscreenTri.vs emits FragPos in clip space [-1, 1] (not [0, 1]).
  // The oversized triangle extends to [-1, 3]; discard the overshoot so we
  // only write the visible [-1, 1] quadrant, then remap to [0, 1] UVs for
  // full-res depth: the half-res VolumetricFogHalfDepth is sized to match
  // the half-res destination FBO, so [0,1] UVs index it directly.
  vec2 ndc = FragPos.xy;
  if (ndc.x > 1.0f || ndc.y > 1.0f)
    discard;
  vec2 uv = ndc * 0.5f + 0.5f;
  OutColor = ComputeVolumetricFog(uv);
}
