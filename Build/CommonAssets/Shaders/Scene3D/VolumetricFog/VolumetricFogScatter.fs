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
uniform vec3 GlobalAmbient;

uniform bool ShadowMapEnabled;
uniform bool UseCascadedDirectionalShadows;
uniform bool EnableCascadedShadows;
uniform float ShadowBiasMin;
uniform float ShadowBiasMax;
uniform int ShadowSamples;
uniform float ShadowFilterRadius;

// Debug visualization mode:
//   0  = normal scatter output (default)
//   1  = solid magenta (pipeline smoke test: proves scatter -> upscale -> composite chain is live)
//   2  = volume hit mask (green if view ray intersects any fog OBB, red if not)
//   3  = average primary-directional shadow factor along the march (grayscale; bright=lit, dark=shadowed)
//   4  = accumulated optical depth (red intensity; brighter = denser march)
//   5  = phase function at march midpoint (blue intensity; HG peaked toward sun direction)
//   6  = raw accumulated scatter (rgb, no composite), useful to see if scatter is non-zero
//   7  = density inputs (red = avg density * 32, green = avg noise mask, blue = avg edge mask)
//   8  = shadow path state (red = shadow disabled, green = cascade coords in bounds, blue = fallback coords in bounds)
//   9  = shadow factor sampled AT THE SURFACE (depth-reconstructed WS, no march). Grayscale.
//        If this looks like the real shadow map projected onto scene geometry, shadow sampling is correct;
//        if not, the shadow sampling function itself is broken.
//   10 = cascade index at surface pos (red=0, green=1, blue=2, yellow=3, white=out-of-range, black=no cascade).
//        Helps verify cascade selection and texture binding.
//   11 = shadow coord UV at surface pos (R=u, G=v, B=z). Should look like the shadow map's UVs swept
//        across visible geometry. If it's garbage or flat, the cascade matrix is wrong.
//   16 = marched fog distance normalized by VolumetricFog.MaxDistance (red).
// All debug modes write alpha = 0 so the composite `hdrScene * a + rgb` replaces the scene with the debug color.
uniform int VolumetricFogDebugMode;

// Use the shared LightStructs definitions so DirLight stays in lock-step with
// the C# uniform upload layout (CascadeBlendWidths, etc.). A locally inlined
// copy here drifts whenever the shared struct changes and silently mismaps
// uniforms by name lookup.
#pragma snippet "LightStructs"

// Mirror the legacy deferred-light upload path (`LightData`) because that is
// the same directional-light uniform surface the scene light-combine pass uses.
// The volumetric fog scatter pass only needs the first directional light.
const int VOLUMETRIC_FOG_MAX_CASCADES = 8;

struct LegacyDirLight
{
  vec3 Color;
  float DiffuseIntensity;
  mat4 WorldToLightInvViewMatrix;
  mat4 WorldToLightProjMatrix;
  mat4 WorldToLightSpaceMatrix;
  vec3 Direction;
  float CascadeSplits[VOLUMETRIC_FOG_MAX_CASCADES];
  float CascadeBlendWidths[VOLUMETRIC_FOG_MAX_CASCADES];
  mat4 CascadeMatrices[VOLUMETRIC_FOG_MAX_CASCADES];
  int CascadeCount;
};

uniform LegacyDirLight LightData;

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

const vec3 VolumetricFogNoiseDomainOffset = vec3(17.37f, 41.13f, 29.91f);

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
float DistanceToVolumeBounds(vec3 localPos, vec3 halfExtents)
{
  vec3 safeExtents = max(halfExtents, vec3(0.0001f));
  vec3 distanceToFace = safeExtents - abs(localPos);
  return min(min(distanceToFace.x, distanceToFace.y), distanceToFace.z);
}
float ComputeNoisyEdgeFade(float distanceToBounds, float edgeFade, float noiseValue, float noiseAmount)
{
  if (distanceToBounds <= 0.0f)
    return 0.0f;

  float fadeDistance = max(edgeFade, 0.0f);
  if (fadeDistance <= 0.0001f)
    return 1.0f;

  float edgeErosion = fadeDistance * 0.85f * saturate(noiseAmount) * (1.0f - clamp(noiseValue, 0.0f, 1.0f));
  float noisyDistance = max(distanceToBounds - edgeErosion, 0.0f);
  return smoothstep(0.0f, fadeDistance, noisyDistance);
}
float ComputeRayIntervalFade(int index, vec3 rayDirWS, float sampleT, float tNear, float tFar, bool fadeRayEntry, bool fadeRayExit, float noiseValue, float noiseAmount)
{
  if (tFar <= tNear)
    return 0.0f;

  float edgeFade = max(VolumetricFogHalfExtentsEdgeFade[index].w, 0.0f);
  if (edgeFade <= 0.0001f)
    return 1.0f;

  vec3 localRayDir = (VolumetricFogWorldToLocal[index] * vec4(rayDirWS, 0.0f)).xyz;
  float edgeFadeOnRay = edgeFade / max(length(localRayDir), 1e-5f);
  float distanceToEntry = fadeRayEntry ? sampleT - tNear : edgeFadeOnRay;
  float distanceToExit = fadeRayExit ? tFar - sampleT : edgeFadeOnRay;
  float distanceToRayBounds = max(min(distanceToEntry, distanceToExit), 0.0f);
  float edgeErosion = edgeFadeOnRay * 0.85f * saturate(noiseAmount) * (1.0f - clamp(noiseValue, 0.0f, 1.0f));
  float noisyDistance = max(distanceToRayBounds - edgeErosion, 0.0f);
  return smoothstep(0.0f, edgeFadeOnRay, noisyDistance);
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

  int cascadeCount = min(LightData.CascadeCount, VOLUMETRIC_FOG_MAX_CASCADES);
  if (cascadeCount <= 0)
    return -1;

  float viewDepth = ViewDepthFromWorldPos(samplePosWS);
  for (int i = 0; i < cascadeCount; ++i)
  {
    if (viewDepth <= LightData.CascadeSplits[i])
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

  // Volumetric shadow bias is intentionally tiny: the sample is a 3D point in
  // open air (no surface normal, no grazing-angle acne risk), so we don't need
  // the surface-tuned bias. A large bias tuned for the forward pass can wash
  // out thin shafts because fog samples near a caster get classified as lit.
  float bias = max(ShadowBiasMin * 0.25f, 1e-5f);
  int cascadeIndex = GetPrimaryDirectionalCascadeIndex(samplePosWS);
  if (cascadeIndex >= 0)
  {
    vec3 cascadeCoord = ProjectShadowCoord(LightData.CascadeMatrices[cascadeIndex], samplePosWS);
    if (ShadowCoordInBounds(cascadeCoord))
    {
      float filterRadius = ShadowFilterRadius * (1.0f + float(cascadeIndex) * 0.35f);
      return ShadowSamples <= 1
        ? SampleShadowMapArraySimple(ShadowMapArray, cascadeCoord, float(cascadeIndex), bias)
        : SampleShadowMapArrayTent4(ShadowMapArray, cascadeCoord, float(cascadeIndex), bias, filterRadius);
    }
  }

  vec3 shadowCoord = ProjectShadowCoord(LightData.WorldToLightSpaceMatrix, samplePosWS);
  if (!ShadowCoordInBounds(shadowCoord))
    return 1.0f;

  return ShadowSamples <= 1
    ? SampleShadowMapSimple(ShadowMap, shadowCoord, bias)
    : SampleShadowMapTent4(ShadowMap, shadowCoord, bias, ShadowFilterRadius);
}
float EvaluateVolumeDensityTerms(int index, vec3 samplePosWS, out float edgeMask, out float noiseMask, out float noiseValue, out float noiseAmount)
{
  vec3 localPos = (VolumetricFogWorldToLocal[index] * vec4(samplePosWS, 1.0f)).xyz;
  vec3 halfExtents = VolumetricFogHalfExtentsEdgeFade[index].xyz;
  float distanceToBounds = DistanceToVolumeBounds(localPos, halfExtents);
  if (distanceToBounds <= 0.0f)
  {
    edgeMask = 0.0f;
    noiseMask = 0.0f;
    noiseValue = 0.0f;
    noiseAmount = 0.0f;
    return 0.0f;
  }

  vec4 noiseParams = VolumetricFogNoiseScaleThreshold[index];
  noiseValue = 1.0f;
  noiseAmount = saturate(noiseParams.z);
  noiseMask = 1.0f;
  if (noiseParams.x > 0.0f && noiseAmount > 0.0f)
  {
    vec3 noiseSamplePos = localPos * noiseParams.x
      + VolumetricFogNoiseOffsetAmount[index].xyz
      + VolumetricFogNoiseVelocity[index].xyz * RenderTime
      + VolumetricFogNoiseDomainOffset;
    noiseValue = clamp(fbm3(noiseSamplePos), 0.0f, 1.0f);
    float thresholdMask = smoothstep(noiseParams.y - 0.15f, noiseParams.y + 0.15f, noiseValue);
    noiseMask = mix(1.0f, thresholdMask, noiseAmount);
  }

  edgeMask = ComputeNoisyEdgeFade(distanceToBounds, VolumetricFogHalfExtentsEdgeFade[index].w, noiseValue, noiseAmount);
  return VolumetricFogColorDensity[index].w * edgeMask * noiseMask;
}
float EvaluateVolumeDensity(int index, vec3 samplePosWS)
{
  float edgeMask;
  float noiseMask;
  float noiseValue;
  float noiseAmount;
  return EvaluateVolumeDensityTerms(index, samplePosWS, edgeMask, noiseMask, noiseValue, noiseAmount);
}
vec3 GetPrimaryDirectionalShadowDebugState(vec3 samplePosWS)
{
  float shadowEnabled = ShadowMapEnabled ? 1.0f : 0.0f;
  float cascadeInBounds = 0.0f;
  float fallbackInBounds = 0.0f;

  if (DirLightCount <= 0)
    return vec3(shadowEnabled, cascadeInBounds, fallbackInBounds);

  int cascadeIndex = GetPrimaryDirectionalCascadeIndex(samplePosWS);
  if (cascadeIndex >= 0)
  {
    vec3 cascadeCoord = ProjectShadowCoord(LightData.CascadeMatrices[cascadeIndex], samplePosWS);
    cascadeInBounds = ShadowCoordInBounds(cascadeCoord) ? 1.0f : 0.0f;
  }

  vec3 shadowCoord = ProjectShadowCoord(LightData.WorldToLightSpaceMatrix, samplePosWS);
  fallbackInBounds = ShadowCoordInBounds(shadowCoord) ? 1.0f : 0.0f;
  return vec3(shadowEnabled, cascadeInBounds, fallbackInBounds);
}
vec3 EvaluateVolumeLighting(int index, vec3 viewToCamera, float shadowFactor)
{
  vec3 ambientLighting = GlobalAmbient * 0.35f;
  float lightContribution = VolumetricFogLightParams[index].x;
  if (DirLightCount <= 0 || lightContribution <= 0.0f)
    return ambientLighting;

  vec3 lightDir = normalize(-LightData.Direction);
  vec3 lightColor = LightData.Color * LightData.DiffuseIntensity;
  float phase = PhaseHenyeyGreenstein(dot(viewToCamera, lightDir), VolumetricFogLightParams[index].y);
  vec3 directLighting = lightColor * shadowFactor * lightContribution * phase * 4.0f;
  return ambientLighting + directLighting;
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
  // Debug smoke test: solid magenta with alpha 0 so the composite replaces the scene.
  if (VolumetricFogDebugMode == 1)
    return vec4(1.0f, 0.0f, 1.0f, 0.0f);

  if (!VolumetricFog.Enabled || VolumetricFog.VolumeCount <= 0 || VolumetricFog.Intensity <= 0.0f || VolumetricFog.MaxDistance <= 0.0f)
  {
    // In non-hit debug modes we still want to know why: emit dark red so it's obvious.
    if (VolumetricFogDebugMode != 0)
      return vec4(0.25f, 0.0f, 0.0f, 0.0f);
    return vec4(0.0f, 0.0f, 0.0f, 1.0f);
  }

  vec2 sourceUv = clamp(uv, vec2(0.0f), vec2(1.0f));
  float rawDepth = texture(VolumetricFogHalfDepth, sourceUv).r;
  float resolvedDepth = XRENGINE_ResolveDepth(rawDepth);

  // Diagnostic modes 9-15 operate directly on the depth-reconstructed SURFACE
  // world position, bypassing the march, volume intersection, and density logic.
  // They isolate the shadow-sampling path so we can tell whether the primary
  // bug is in shadow projection vs. everything else in the fog pipeline.
  //
  // Modes 12-15 visualize raw uniforms (not computed shadow values) so we can
  // verify that SetForwardLightingUniforms is actually landing on the scatter
  // program. Modes 13 and 14 were redesigned 2026-04-23 after the previous
  // squash-based visualizers mapped valid cascade-matrix values into the
  // neutral-gray zone and misled diagnosis.
  if (VolumetricFogDebugMode >= 9 && VolumetricFogDebugMode <= 15)
  {
    // Mode 12: LightData.Direction as color. Non-zero = light uniform
    // arrived. Expected: approximately (0.5 + dir*0.5) per component.
    if (VolumetricFogDebugMode == 12)
    {
      vec3 d = LightData.Direction;
      return vec4(d * 0.5f + 0.5f, 0.0f);
    }
    // Mode 15: CascadeSplits[0] / 100 (red) and CascadeCount / 4 (green). Lets
    // us see whether the splits array was uploaded. (Kept before the surface-
    // dependent modes so it works even when the screen is entirely sky.)
    if (VolumetricFogDebugMode == 15)
    {
      float split = LightData.CascadeSplits[0];
      float normSplit = split / 100.0f;
      float normCount = float(LightData.CascadeCount) / 4.0f;
      return vec4(clamp(normSplit, 0.0f, 1.0f), clamp(normCount, 0.0f, 1.0f), 0.0f, 0.0f);
    }

    if (resolvedDepth >= 0.999999f)
    {
      // Sky pixel: no surface. Emit near-black so the scene pixel is replaced but clearly
      // distinguishable from an actual "black = shadowed" sample.
      return vec4(0.02f, 0.0f, 0.02f, 0.0f);
    }
    vec3 surfacePosWS = XRENGINE_WorldPosFromDepthRaw(rawDepth, sourceUv, InverseProjMatrix, InverseViewMatrix);

    // Mode 13 (redesigned): visualize reconstructed surface world position directly
    // via fract() so we see a wrapping gradient across the scene. If reconstruction
    // is correct the scene geometry shows clear gradients matching real world dimensions;
    // if InverseProjMatrix/InverseViewMatrix are stale/identity the result is flat or
    // exhibits screen-space (not world-space) patterns.
    if (VolumetricFogDebugMode == 13)
    {
      vec3 wrapped = fract(surfacePosWS * 0.1f);
      return vec4(wrapped, 0.0f);
    }
    // Mode 14 (redesigned): project a canonical world-space point (the origin)
    // through CascadeMatrices[0] and visualize the resulting clip-space XYZ.
    //   If CascadeMatrices[0] is identity             → output = (0.5, 0.5, 0.5) gray
    //   If CascadeMatrices[0] is a real view·proj     → some bounded non-gray color
    //   If CascadeMatrices[0] is all zeros            → output = (NaN) but alpha=0 path clamps
    // This bypasses every other potential source of variance (surface reconstruction,
    // view matrix, depth reconstruction) so the only remaining signal is the matrix itself.
    if (VolumetricFogDebugMode == 14)
    {
      vec4 originClip = LightData.CascadeMatrices[0] * vec4(0.0f, 0.0f, 0.0f, 1.0f);
      // Safe divide: a well-formed VP has w near 1 for origin; an identity matrix yields
      // w=1 as well. A degenerate matrix may give w=0 → clamp to avoid NaN.
      float wSafe = (abs(originClip.w) < 1e-6f) ? 1.0f : originClip.w;
      vec3 ndc = originClip.xyz / wSafe;
      return vec4(ndc * 0.5f + 0.5f, 0.0f);
    }

    if (VolumetricFogDebugMode == 9)
    {
      float s = EvaluatePrimaryDirectionalShadow(surfacePosWS);
      return vec4(s, s, s, 0.0f);
    }
    if (VolumetricFogDebugMode == 10)
    {
      int ci = GetPrimaryDirectionalCascadeIndex(surfacePosWS);
      if (ci < 0) return vec4(0.0f, 0.0f, 0.0f, 0.0f);
      if (ci == 0) return vec4(1.0f, 0.0f, 0.0f, 0.0f);
      if (ci == 1) return vec4(0.0f, 1.0f, 0.0f, 0.0f);
      if (ci == 2) return vec4(0.0f, 0.0f, 1.0f, 0.0f);
      if (ci == 3) return vec4(1.0f, 1.0f, 0.0f, 0.0f);
      return vec4(1.0f, 1.0f, 1.0f, 0.0f);
    }
    // Mode 11: visualize cascade shadow coord (or 2D fallback coord if no cascade).
    int ci = GetPrimaryDirectionalCascadeIndex(surfacePosWS);
    vec3 coord = ci >= 0
      ? ProjectShadowCoord(LightData.CascadeMatrices[ci], surfacePosWS)
      : ProjectShadowCoord(LightData.WorldToLightSpaceMatrix, surfacePosWS);
    return vec4(coord, 0.0f);
  }

  float rayLength = VolumetricFog.MaxDistance;
  if (resolvedDepth < 0.999999f)
  {
    vec3 surfacePosWS = XRENGINE_WorldPosFromDepthRaw(rawDepth, sourceUv, InverseProjMatrix, InverseViewMatrix);
    rayLength = min(rayLength, distance(CameraPosition, surfacePosWS));
  }

  if (rayLength <= 0.0f)
  {
    if (VolumetricFogDebugMode != 0)
      return vec4(0.5f, 0.0f, 0.0f, 0.0f);
    return vec4(0.0f, 0.0f, 0.0f, 1.0f);
  }

  vec3 rayDir;
  if (resolvedDepth >= 0.999999f)
  {
    // Use the raw far-plane depth for the current depth convention.
    float farRawDepth = DepthMode == 1 ? 0.0f : 1.0f;
    vec3 worldFar = XRENGINE_WorldPosFromDepthRaw(farRawDepth, sourceUv, InverseProjMatrix, InverseViewMatrix);
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
  float volumeTNear[MaxVolumetricFogVolumes];
  float volumeTFar[MaxVolumetricFogVolumes];
  bool volumeFadeEntry[MaxVolumetricFogVolumes];
  bool volumeFadeExit[MaxVolumetricFogVolumes];
  for (int volumeIndex = 0; volumeIndex < MaxVolumetricFogVolumes; ++volumeIndex)
  {
    volumeTNear[volumeIndex] = 0.0f;
    volumeTFar[volumeIndex] = -1.0f;
    volumeFadeEntry[volumeIndex] = false;
    volumeFadeExit[volumeIndex] = false;
  }

  bool anyHit = false;
  for (int volumeIndex = 0; volumeIndex < VolumetricFog.VolumeCount; ++volumeIndex)
  {
    float tNear, tFar;
    if (!IntersectVolumeOBB(volumeIndex, CameraPosition, rayDir, tNear, tFar))
      continue;
    bool fadeRayEntry = tNear > 0.0f;
    bool fadeRayExit = tFar <= rayLength + 1e-4f;
    tNear = max(tNear, 0.0f);
    tFar  = min(tFar, rayLength);
    if (tFar <= tNear)
      continue;
    volumeTNear[volumeIndex] = tNear;
    volumeTFar[volumeIndex] = tFar;
    volumeFadeEntry[volumeIndex] = fadeRayEntry;
    volumeFadeExit[volumeIndex] = fadeRayExit;
    unionTNear = min(unionTNear, tNear);
    unionTFar  = max(unionTFar, tFar);
    anyHit = true;
  }

  if (VolumetricFogDebugMode == 2)
    return vec4(anyHit ? 0.0f : 1.0f, anyHit ? 1.0f : 0.0f, 0.0f, 0.0f);

  if (!anyHit || unionTFar <= unionTNear)
  {
    if (VolumetricFogDebugMode != 0)
      return vec4(0.0f, 0.0f, 0.25f, 0.0f); // dark blue = ray missed all volumes
    return vec4(0.0f, 0.0f, 0.0f, 1.0f);
  }

  float stepSize = max(VolumetricFog.StepSize, 0.25f);
  float marchLength = unionTFar - unionTNear;
  int stepCount = max(1, int(ceil(marchLength / stepSize)));

  if (VolumetricFogDebugMode == 16)
  {
    float normalizedMarchLength = VolumetricFog.MaxDistance > 0.0f
      ? marchLength / VolumetricFog.MaxDistance
      : 0.0f;
    return vec4(saturate(normalizedMarchLength), 0.0f, 0.0f, 0.0f);
  }

  // Per-pixel blue-noise offset on the march start position. ALWAYS apply this
  // spatial dither regardless of JitterStrength: if every pixel started at the
  // same offset within its ray, the per-step sample positions across neighboring
  // pixels would only differ by the (very smooth) ray-direction delta, and the
  // 30+ step average would collapse to a near-constant per-pixel value — making
  // the entire screen look like one solid color (no shafts, no density variation).
  // JitterStrength now controls only the *temporal* component: when > 0 the IGN
  // seed is re-mixed each frame so the per-pixel offset moves frame-to-frame
  // (TAA / temporal accumulation can then average it out). When 0 the per-pixel
  // pattern is stable so there is no flicker.
  float temporalSeedOffset = fract(RenderTime * 7.0f) * 64.0f * VolumetricFog.JitterStrength;
  float ign = interleavedGradientNoise(gl_FragCoord.xy + temporalSeedOffset);
  float t = unionTNear + ign * stepSize;
  vec3 accumulatedScattering = vec3(0.0f);
  float transmittance = 1.0f;

  // Debug accumulators.
  float debugShadowSum = 0.0f;
  int debugShadowSamples = 0;
  float debugOpticalDepth = 0.0f;
  float debugPhaseSample = 0.0f;
  float debugDensitySum = 0.0f;
  float debugNoiseMaskSum = 0.0f;
  float debugEdgeMaskSum = 0.0f;
  int debugDensitySamples = 0;
  vec3 debugShadowStateSum = vec3(0.0f);

  for (int stepIndex = 0; stepIndex < stepCount; ++stepIndex)
  {
    if (t >= unionTFar || transmittance <= 0.01f)
      break;

    float currentStep = min(stepSize, unionTFar - t);
    float sampleT = t + currentStep * 0.5f;
    vec3 samplePosWS = CameraPosition + rayDir * sampleT;
    vec3 stepScattering = vec3(0.0f);
    float stepExtinction = 0.0f;
    vec3 viewToCamera = -rayDir;
    float shadowFactor = EvaluatePrimaryDirectionalShadow(samplePosWS);
    debugShadowSum += shadowFactor;
    debugShadowSamples += 1;
    if (VolumetricFogDebugMode == 8)
      debugShadowStateSum += GetPrimaryDirectionalShadowDebugState(samplePosWS);

    for (int volumeIndex = 0; volumeIndex < VolumetricFog.VolumeCount; ++volumeIndex)
    {
      float edgeMask;
      float noiseMask;
      float noiseValue;
      float noiseAmount;
      float densityTerms = EvaluateVolumeDensityTerms(volumeIndex, samplePosWS, edgeMask, noiseMask, noiseValue, noiseAmount);
      float rayEdgeMask = ComputeRayIntervalFade(volumeIndex, rayDir, sampleT, volumeTNear[volumeIndex], volumeTFar[volumeIndex], volumeFadeEntry[volumeIndex], volumeFadeExit[volumeIndex], noiseValue, noiseAmount);
      float density = densityTerms * rayEdgeMask * VolumetricFog.Intensity;
      debugDensitySum += density;
      debugNoiseMaskSum += noiseMask;
      debugEdgeMaskSum += edgeMask * rayEdgeMask;
      debugDensitySamples += 1;
      if (density <= 0.0f)
        continue;

      vec3 albedo = VolumetricFogColorDensity[volumeIndex].rgb;
      vec3 lighting = EvaluateVolumeLighting(volumeIndex, viewToCamera, shadowFactor);
      stepScattering += albedo * lighting * density;
      stepExtinction += density;

      if (stepIndex == stepCount / 2 && debugPhaseSample == 0.0f && DirLightCount > 0)
      {
        vec3 lightDir = normalize(-LightData.Direction);
        debugPhaseSample = PhaseHenyeyGreenstein(dot(viewToCamera, lightDir), VolumetricFogLightParams[volumeIndex].y);
      }
    }

    if (stepExtinction > 0.0f)
    {
      accumulatedScattering += stepScattering * currentStep * transmittance;
      transmittance *= exp(-stepExtinction * currentStep);
      debugOpticalDepth += stepExtinction * currentStep;
    }

    t += currentStep;
  }

  if (VolumetricFogDebugMode == 3)
  {
    float avg = debugShadowSamples > 0 ? debugShadowSum / float(debugShadowSamples) : 1.0f;
    return vec4(avg, avg, avg, 0.0f);
  }
  if (VolumetricFogDebugMode == 4)
    return vec4(saturate(debugOpticalDepth), 0.0f, 0.0f, 0.0f);
  if (VolumetricFogDebugMode == 5)
    return vec4(0.0f, 0.0f, saturate(debugPhaseSample), 0.0f);
  if (VolumetricFogDebugMode == 6)
    return vec4(accumulatedScattering, 0.0f);
  if (VolumetricFogDebugMode == 7)
  {
    float avgDensity = debugDensitySamples > 0 ? debugDensitySum / float(debugDensitySamples) : 0.0f;
    float avgNoiseMask = debugDensitySamples > 0 ? debugNoiseMaskSum / float(debugDensitySamples) : 0.0f;
    float avgEdgeMask = debugDensitySamples > 0 ? debugEdgeMaskSum / float(debugDensitySamples) : 0.0f;
    return vec4(saturate(avgDensity * 32.0f), saturate(avgNoiseMask), saturate(avgEdgeMask), 0.0f);
  }
  if (VolumetricFogDebugMode == 8)
  {
    vec3 avgShadowState = debugShadowSamples > 0 ? debugShadowStateSum / float(debugShadowSamples) : vec3(0.0f);
    return vec4(1.0f - saturate(avgShadowState.x), saturate(avgShadowState.y), saturate(avgShadowState.z), 0.0f);
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
