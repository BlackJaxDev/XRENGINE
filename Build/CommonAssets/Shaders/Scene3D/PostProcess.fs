#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D HDRSceneTex; //HDR scene color
uniform sampler2D BloomBlurTexture; //Bloom
uniform sampler2D DepthView; //Depth
uniform usampler2D StencilView; //Stencil

// 1x1 R32F texture containing the current exposure value (GPU-driven auto exposure)
uniform sampler2D AutoExposureTex;
uniform bool UseGpuAutoExposure;
uniform vec3 CameraPosition;
uniform mat4 ViewMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform int DirLightCount;
uniform float RenderTime;
uniform sampler2D ShadowMap;
uniform sampler2DArray ShadowMapArray;
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

uniform vec3 HighlightColor = vec3(1.0f, 0.0f, 1.0f); // Bright magenta for visibility
uniform bool OutputHDR;

struct VignetteStruct
{
    vec3 Color;
    float Intensity;
    float Power;
};
uniform VignetteStruct Vignette;

struct ColorGradeStruct
{
    vec3 Tint;

    float Exposure;
    float Contrast;
    float Gamma;

    float Hue; //1.0f = no change, 0.0f = red
    float Saturation; //1.0f = no change, 0.0f = grayscale
    float Brightness; //1.0f = no change, 0.0f = black
};
uniform ColorGradeStruct ColorGrade;

float GetExposure()
{
  if (UseGpuAutoExposure)
  {
    float e = texelFetch(AutoExposureTex, ivec2(0, 0), 0).r;
    if (!(isnan(e) || isinf(e)) && e > 0.0)
      return e;
  }
  return ColorGrade.Exposure;
}

uniform float ChromaticAberrationIntensity;

struct DepthFogStruct
{
    float Intensity; //0.0f = no fog, 1.0f = full fog
    float Start; //Start distance of fog
    float End; //End distance of fog
    vec3 Color; //Color of fog
};
uniform DepthFogStruct DepthFog;

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

// Lens distortion mode: 0=None, 1=Radial, 2=RadialAutoFromFOV, 3=Panini, 4=BrownConrady
uniform int LensDistortionMode;
uniform float LensDistortionIntensity;
uniform vec2 LensDistortionCenter;
uniform float PaniniDistance;
uniform float PaniniCrop;
uniform vec2 PaniniViewExtents; // tan(fov/2) * aspect, tan(fov/2)

// Brown-Conrady coefficients
uniform vec3 BrownConradyRadial;     // k1,k2,k3
uniform vec2 BrownConradyTangential; // p1,p2

// Bloom combine controls
uniform float BloomStrength = 0.15;
uniform int BloomStartMip = 1;
uniform int BloomEndMip = 1;
uniform float BloomLodWeights[5] = float[](0.0, 1.0, 0.0, 0.0, 0.0);
uniform bool DebugBloomOnly = false;

uniform int DepthMode;

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

vec3 RGBtoHSV(vec3 c)
{
    vec4 K = vec4(0.0f, -1.0f / 3.0f, 2.0f / 3.0f, -1.0f);
    vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
    vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));

    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10f;
    return vec3(abs(q.z + (q.w - q.y) / (6.0f * d + e)), d / (q.x + e), q.x);
}
vec3 HSVtoRGB(vec3 c)
{
    vec4 K = vec4(1.0f, 2.0f / 3.0f, 1.0f / 3.0f, 3.0f);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0f - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0f, 1.0f), c.y);
}
vec3 ApplyHsvColorGrade(vec3 sceneColor)
{
  if (ColorGrade.Hue == 1.0f && ColorGrade.Saturation == 1.0f && ColorGrade.Brightness == 1.0f)
    return sceneColor;

  vec3 hsv = RGBtoHSV(max(sceneColor, vec3(0.0f)));
  hsv.x = fract(hsv.x * ColorGrade.Hue);
  hsv.y = clamp(hsv.y * ColorGrade.Saturation, 0.0f, 1.0f);
  hsv.z = max(hsv.z * ColorGrade.Brightness, 0.0f);
  return HSVtoRGB(hsv);
}
float rand(vec2 coord)
{
    return fract(sin(dot(coord, vec2(12.9898f, 78.233f))) * 43758.5453f);
}
float interleavedGradientNoise(vec2 pixelCoord)
{
  return fract(52.9829189f * fract(dot(pixelCoord, vec2(0.06711056f, 0.00583715f))));
}
float saturate(float value)
{
  return clamp(value, 0.0f, 1.0f);
}
vec3 ApplyVignette(vec3 sceneColor, vec2 uv)
{
  if (Vignette.Intensity <= 0.0f)
    return sceneColor;

  vec2 centeredUv = (uv - LensDistortionCenter) * 2.0f;
  float radius = saturate(length(centeredUv) * 0.70710678f);
  float vignetteFactor = pow(radius, max(Vignette.Power, 0.0001f)) * saturate(Vignette.Intensity);
  return mix(sceneColor, Vignette.Color, vignetteFactor);
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
vec2 ApplyLensDistortionByMode(vec2 uv);
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
  lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(0.5f, -0.5f) * radius).r ? 1.0f : 0.0f;
  lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(-0.5f, 0.5f) * radius).r ? 1.0f : 0.0f;
  lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(0.5f, 0.5f) * radius).r ? 1.0f : 0.0f;

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
  lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(0.5f, -0.5f) * radius, layer)).r ? 1.0f : 0.0f;
  lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(-0.5f, 0.5f) * radius, layer)).r ? 1.0f : 0.0f;
  lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(0.5f, 0.5f) * radius, layer)).r ? 1.0f : 0.0f;

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
vec4 ComputeVolumetricFog(vec2 uv)
{
  if (!VolumetricFog.Enabled || VolumetricFog.VolumeCount <= 0 || VolumetricFog.Intensity <= 0.0f || VolumetricFog.MaxDistance <= 0.0f)
    return vec4(0.0f, 0.0f, 0.0f, 1.0f);

  vec2 sourceUv = clamp(ApplyLensDistortionByMode(uv), vec2(0.0f), vec2(1.0f));
  float rawDepth = texture(DepthView, sourceUv).r;
  float resolvedDepth = XRENGINE_ResolveDepth(rawDepth);

  float rayLength = VolumetricFog.MaxDistance;
  if (resolvedDepth < 0.999999f)
  {
    vec3 surfacePosWS = XRENGINE_WorldPosFromDepthRaw(rawDepth, sourceUv, InverseProjMatrix, InverseViewMatrix);
    rayLength = min(rayLength, distance(CameraPosition, surfacePosWS));
  }

  if (rayLength <= 0.0f)
    return vec4(0.0f, 0.0f, 0.0f, 1.0f);

  float stepSize = max(VolumetricFog.StepSize, 0.25f);
  int stepCount = max(1, int(ceil(rayLength / stepSize)));
  vec3 rayDir = normalize(XRENGINE_WorldPosFromDepthRaw(rawDepth, sourceUv, InverseProjMatrix, InverseViewMatrix) - CameraPosition);
  if (resolvedDepth >= 0.999999f)
  {
    vec4 viewFar = InverseProjMatrix * vec4(sourceUv * 2.0f - 1.0f, 1.0f, 1.0f);
    viewFar /= max(viewFar.w, 0.0001f);
    vec3 worldFar = (InverseViewMatrix * viewFar).xyz;
    rayDir = normalize(worldFar - CameraPosition);
  }

  // Keep the jitter centered around the midpoint of the first step so it breaks up
  // marching bands without imprinting a full-step extinction bias onto opaque surfaces.
  float jitter = (interleavedGradientNoise(gl_FragCoord.xy) - 0.5f) * stepSize * VolumetricFog.JitterStrength;
  float t = max(0.0f, 0.5f * stepSize + jitter);
  vec3 accumulatedScattering = vec3(0.0f);
  float transmittance = 1.0f;

  for (int stepIndex = 0; stepIndex < stepCount; ++stepIndex)
  {
    if (t >= rayLength || transmittance <= 0.01f)
      break;

    float currentStep = min(stepSize, rayLength - t);
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
float GetStencilHighlightIntensity(vec2 uv)
{
    int outlineSize = 3; // Increased from 1 to make outline more visible
    ivec2 texSize = textureSize(HDRSceneTex, 0);
    vec2 texelSize = 1.0f / texSize;
    vec2 texelX = vec2(texelSize.x, 0.0f);
    vec2 texelY = vec2(0.0f, texelSize.y);
    uint stencilCurrent = texture(StencilView, uv).r;
    uint selectionBits = stencilCurrent & 1;
    uint diff = 0;
    vec2 zero = vec2(0.0f);

    //Check neighboring stencil texels that indicate highlighted/selected
    for (int i = 1; i <= outlineSize; ++i)
    {
          vec2 yPos = clamp(uv + texelY * i, zero, uv);
          vec2 yNeg = clamp(uv - texelY * i, zero, uv);
          vec2 xPos = clamp(uv + texelX * i, zero, uv);
          vec2 xNeg = clamp(uv - texelX * i, zero, uv);
          diff += (texture(StencilView, yPos).r & 1) - selectionBits;
          diff += (texture(StencilView, yNeg).r & 1) - selectionBits;
          diff += (texture(StencilView, xPos).r & 1) - selectionBits;
          diff += (texture(StencilView, xNeg).r & 1) - selectionBits;
    }
    return clamp(float(diff), 0.0f, 1.0f);
}

// Tonemapping selector and shared tonemap operators
#include "../Snippets/ToneMapping.glsl"

uniform int TonemapType = XRENGINE_TONEMAP_MOBIUS;
uniform float MobiusTransition = 0.6f;

vec2 ApplyLensDistortion(vec2 uv, float intensity, vec2 center)
{
  uv -= center;
    float uva = atan(uv.x, uv.y);
    float uvd = sqrt(dot(uv, uv));
    uvd *= 1.0 + intensity * uvd * uvd;
  return center + vec2(sin(uva), cos(uva)) * uvd;
}

vec2 ApplyBrownConrady(vec2 uvCentered)
{
  // Work in normalized coordinates around 0.
  vec2 x = uvCentered * 2.0 - 1.0;
  float r2 = dot(x, x);
  float r4 = r2 * r2;
  float r6 = r4 * r2;

  float k1 = BrownConradyRadial.x;
  float k2 = BrownConradyRadial.y;
  float k3 = BrownConradyRadial.z;
  float p1 = BrownConradyTangential.x;
  float p2 = BrownConradyTangential.y;

  float radial = 1.0 + k1 * r2 + k2 * r4 + k3 * r6;
  vec2 tangential = vec2(
    2.0 * p1 * x.x * x.y + p2 * (r2 + 2.0 * x.x * x.x),
    p1 * (r2 + 2.0 * x.y * x.y) + 2.0 * p2 * x.x * x.y);

  vec2 xd = x * radial + tangential;
  return xd * 0.5 + 0.5;
}

// Panini projection - preserves vertical lines while compressing horizontal periphery
// Based on Unity's implementation from the Stockholm demo team
// d = 1.0 is "unit distance" (simplified), d != 1.0 is "generic" panini
vec2 ApplyPaniniProjection(vec2 view_pos, float d)
{
    // Generic Panini projection
    // Given a point on the image plane, project it onto a cylinder
    // then back onto the image plane from a different viewpoint
    
    float view_dist = 1.0 + d;
    float view_hyp_sq = view_pos.x * view_pos.x + view_dist * view_dist;
    
    float isect_D = view_pos.x * d;
    float isect_discrim = view_hyp_sq - isect_D * isect_D;
    
    float cyl_dist_minus_d = (-isect_D * view_pos.x + view_dist * sqrt(max(isect_discrim, 0.0))) / view_hyp_sq;
    float cyl_dist = cyl_dist_minus_d + d;
    
    vec2 cyl_pos = view_pos * (cyl_dist / view_dist);
    return cyl_pos / (cyl_dist - d);
}

vec2 ApplyLensDistortionByMode(vec2 uv)
{
  // Recenter so principal point maps to UV 0.5,0.5 for distortion models.
  vec2 uvCentered = uv - LensDistortionCenter + vec2(0.5);

    // Mode: 0=None, 1=Radial, 2=RadialAutoFromFOV, 3=Panini, 4=BrownConrady
    if (LensDistortionMode == 1 || LensDistortionMode == 2)
    {
        if (LensDistortionIntensity != 0.0)
      return ApplyLensDistortion(uvCentered, LensDistortionIntensity, vec2(0.5));
    }
    else if (LensDistortionMode == 3)
    {
        if (PaniniDistance > 0.0)
        {
            // Convert UV [0,1] to view position using view extents
            // PaniniViewExtents contains (tan(fov/2) * aspect, tan(fov/2))
            // PaniniCrop is the scale factor for crop-to-fit
            vec2 view_pos = (2.0 * uvCentered - 1.0) * PaniniViewExtents * PaniniCrop;
            vec2 proj_pos = ApplyPaniniProjection(view_pos, PaniniDistance);
            // Convert back to UV
            vec2 proj_ndc = proj_pos / PaniniViewExtents;
            vec2 outCentered = proj_ndc * 0.5 + 0.5;
            return outCentered - vec2(0.5) + LensDistortionCenter;
        }
    }
    else if (LensDistortionMode == 4)
    {
      vec2 outCentered = ApplyBrownConrady(uvCentered);
      return outCentered - vec2(0.5) + LensDistortionCenter;
    }
    return uv;
}

vec3 SampleHDR(vec2 uv)
{
  vec2 duv = ApplyLensDistortionByMode(uv);
  return texture(HDRSceneTex, duv).rgb;
}
vec3 SampleBloom(vec2 uv, float lod)
{
  vec2 duv = ApplyLensDistortionByMode(uv);
  return textureLod(BloomBlurTexture, duv, lod).rgb;
}

void main()
{
  vec2 uv = FragPos.xy;
  if (uv.x > 1.0f || uv.y > 1.0f)
      discard;
  //Normalize uv from [-1, 1] to [0, 1]
  uv = uv * 0.5f + 0.5f;
  
  //Perform HDR operations
  vec3 hdrSceneColor;
  
  //Apply chromatic aberration with screen-space offsets
  if (ChromaticAberrationIntensity > 0.0f)
  {
      // Direction from center of screen
      vec2 dir = uv - LensDistortionCenter;
      // Scale by intensity directly (0-1 range produces visible offset)
      vec2 off = dir * ChromaticAberrationIntensity * 0.1;
  
      // Clamp UVs to avoid sampling outside [0,1]
      vec2 uvR = clamp(uv + off, vec2(0.0f), vec2(1.0f));
      vec2 uvG = uv;
      vec2 uvB = clamp(uv - off, vec2(0.0f), vec2(1.0f));
  
      float r = SampleHDR(uvR).r;
      float g = SampleHDR(uvG).g;
      float b = SampleHDR(uvB).b;
      hdrSceneColor = vec3(r, g, b);
  }
  else
  {
      hdrSceneColor = SampleHDR(uv);
  }
  
  //Add bloom with configurable range/weights, scaled by overall strength
  if (DebugBloomOnly)
  {
    // Diagnostic: 2x2 grid showing bloom texture mip levels 0-3.
    //   Top-left  = mip 0 (scene copy, red border)
    //   Top-right = mip 1 (threshold-filtered downsample, green border)
    //   Bot-left  = mip 2 (further downsample, blue border)
    //   Bot-right = mip 3 (further downsample, yellow border)
    // If mip 0 and mip 1 look identical, either textureLod isn't
    // distinguishing mips (GL_TEXTURE_MAX_LEVEL issue) or the
    // downsample pass is not writing threshold-filtered content.
    int col = uv.x < 0.5 ? 0 : 1;
    int row = uv.y < 0.5 ? 0 : 1;
    int mip = row * 2 + col; // 0=TL, 1=TR, 2=BL, 3=BR
    vec2 cellUV = fract(uv * 2.0);

    // Thin colored border per quadrant for identification.
    float border = 0.005;
    bool onBorder = cellUV.x < border || cellUV.x > (1.0 - border)
                 || cellUV.y < border || cellUV.y > (1.0 - border);
    vec3 borderColors[4] = vec3[](
        vec3(1.0, 0.0, 0.0),   // mip 0: red
        vec3(0.0, 1.0, 0.0),   // mip 1: green
        vec3(0.0, 0.0, 1.0),   // mip 2: blue
        vec3(1.0, 1.0, 0.0)    // mip 3: yellow
    );

    if (onBorder)
    {
        OutColor = vec4(borderColors[mip], 1.0);
    }
    else
    {
        // Sample the bloom texture at the quadrant's mip level using the cell UV.
        vec3 mipColor = textureLod(BloomBlurTexture, cellUV, float(mip)).rgb;
        OutColor = vec4(mipColor, 1.0);
    }
    return;
  }
  else if (BloomStrength > 0.0f)
  {
    int startMip = clamp(BloomStartMip, 0, 4);
    int endMip = clamp(BloomEndMip, startMip, 4);
    for (int lod = startMip; lod <= endMip; ++lod)
    {
      float w = BloomLodWeights[lod];
      if (w > 0.0f)
        hdrSceneColor += SampleBloom(uv, float(lod)) * w * BloomStrength;
    }
  }

  vec4 volumetricFog = ComputeVolumetricFog(uv);
  hdrSceneColor = hdrSceneColor * volumetricFog.a + volumetricFog.rgb;

  //Tone mapping / HDR selection
  vec3 sceneColor;
  if (OutputHDR)
  {
      sceneColor = hdrSceneColor * GetExposure();
  }
  else
  {
      sceneColor = XRENGINE_ApplyToneMap(hdrSceneColor, TonemapType, GetExposure(), ColorGrade.Gamma, MobiusTransition);
  }

  //Apply depth-based fog
  if (DepthFog.Intensity > 0.0f)
  {
      float depth = texture(DepthView, uv).r;
      float fogFactor = clamp((depth - DepthFog.Start) / (DepthFog.End - DepthFog.Start), 0.0f, 1.0f);
      sceneColor = mix(sceneColor, DepthFog.Color, fogFactor * DepthFog.Intensity);
  }

	//Color grading
	sceneColor *= ColorGrade.Tint;

	sceneColor = ApplyHsvColorGrade(sceneColor);
	sceneColor = (sceneColor - 0.5f) * ColorGrade.Contrast + 0.5f;

  //Apply highlight color to selected objects
  float highlight = GetStencilHighlightIntensity(uv);
	sceneColor = mix(sceneColor, HighlightColor, highlight);

  // DEBUG: Visualize raw stencil value - uncomment to see stencil data
  // uint rawStencil = texture(StencilView, uv).r;
  // if ((rawStencil & 1) != 0) sceneColor = vec3(1.0, 0.0, 0.0); // Red where stencil bit 0 is set

	sceneColor = ApplyVignette(sceneColor, uv);

  if (!OutputHDR)
  {
	  //Gamma-correct
	  sceneColor = pow(max(sceneColor, vec3(0.0f)), vec3(1.0f / max(ColorGrade.Gamma, 0.0001f)));

    //Fix subtle banding by applying fine noise
    sceneColor += mix(-0.5f / 255.0f, 0.5f / 255.0f, rand(uv));
  }

	OutColor = vec4(sceneColor, 1.0f);
}
