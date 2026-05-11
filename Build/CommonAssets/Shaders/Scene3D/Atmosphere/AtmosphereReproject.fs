#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D AtmosphereHalfScatter;
uniform sampler2D AtmosphereHalfHistory;
uniform sampler2D AtmosphereHalfDepth;

uniform vec3 CameraPosition;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform int DepthMode;

uniform bool AtmosphereHistoryReady;
uniform mat4 AtmospherePreviousViewProjection;
uniform vec2 AtmosphereHistoryTexelSize;
uniform float AtmosphereMaxDistance;
uniform float AtmosphereTemporalAlpha;
uniform float AtmosphereDepthRejectThreshold;
uniform int AtmosphereDebugMode;

float ResolveDepth(float depth)
{
  return DepthMode == 1 ? (1.0f - depth) : depth;
}

bool IsValidUV(vec2 uv)
{
  return all(greaterThanEqual(uv, vec2(0.0f))) && all(lessThanEqual(uv, vec2(1.0f)));
}

vec3 WorldPosFromDepthRaw(float rawDepth, vec2 uv)
{
  vec4 clipSpacePosition = vec4(vec3(uv, rawDepth) * 2.0f - 1.0f, 1.0f);
  vec4 viewSpacePosition = InverseProjMatrix * clipSpacePosition;
  float safeW = max(abs(viewSpacePosition.w), 1e-5f);
  viewSpacePosition /= safeW * sign(viewSpacePosition.w == 0.0f ? 1.0f : viewSpacePosition.w);
  return (InverseViewMatrix * viewSpacePosition).xyz;
}

float LinearEyeDistance(float rawDepth, vec2 uv)
{
  vec4 clipSpacePosition = vec4(vec3(uv, rawDepth) * 2.0f - 1.0f, 1.0f);
  vec4 viewSpacePosition = InverseProjMatrix * clipSpacePosition;
  float safeW = max(abs(viewSpacePosition.w), 1e-5f);
  return abs(viewSpacePosition.z / safeW);
}

bool ProjectToPreviousUv(vec3 worldPos, out vec2 previousUv)
{
  vec4 previousClip = AtmospherePreviousViewProjection * vec4(worldPos, 1.0f);
  if (abs(previousClip.w) <= 1e-5f)
  {
    previousUv = vec2(0.0f);
    return false;
  }

  vec2 previousNdc = previousClip.xy / previousClip.w;
  previousUv = previousNdc * 0.5f + 0.5f;
  return IsValidUV(previousUv);
}

vec4 ClipToAABB(vec4 value, vec4 boundsMin, vec4 boundsMax)
{
  vec4 center = 0.5f * (boundsMin + boundsMax);
  vec4 extents = 0.5f * (boundsMax - boundsMin) + vec4(1e-5f);
  vec4 offset = value - center;
  vec4 scale = abs(extents / max(abs(offset), vec4(1e-5f)));
  float clipAmount = clamp(min(min(scale.x, scale.y), min(scale.z, scale.w)), 0.0f, 1.0f);
  return center + offset * clipAmount;
}

bool IsNeutralAtmosphere(vec4 value)
{
  return value.a >= 0.9999f && all(lessThanEqual(abs(value.rgb), vec3(1e-5f)));
}

void ComputeNeighborhoodBounds(vec2 uv, out vec4 boundsMin, out vec4 boundsMax)
{
  boundsMin = vec4(1.0e20f);
  boundsMax = vec4(-1.0e20f);

  for (int offsetY = -1; offsetY <= 1; ++offsetY)
  {
    for (int offsetX = -1; offsetX <= 1; ++offsetX)
    {
      vec2 sampleUv = clamp(uv + vec2(float(offsetX), float(offsetY)) * AtmosphereHistoryTexelSize, vec2(0.0f), vec2(1.0f));
      vec4 sampleValue = texture(AtmosphereHalfScatter, sampleUv);
      boundsMin = min(boundsMin, sampleValue);
      boundsMax = max(boundsMax, sampleValue);
    }
  }
}

void main()
{
  vec2 ndc = FragPos.xy;
  if (ndc.x > 1.0f || ndc.y > 1.0f)
    discard;

  vec2 uv = ndc * 0.5f + 0.5f;
  vec4 currentAtmosphere = texture(AtmosphereHalfScatter, uv);

  if (IsNeutralAtmosphere(currentAtmosphere)
    || !AtmosphereHistoryReady
    || AtmosphereMaxDistance <= 0.0f
    || AtmosphereDebugMode != 0)
  {
    OutColor = currentAtmosphere;
    return;
  }

  float rawDepth = texture(AtmosphereHalfDepth, uv).r;
  if (ResolveDepth(rawDepth) >= 0.999999f)
  {
    OutColor = currentAtmosphere;
    return;
  }

  vec3 worldPos = WorldPosFromDepthRaw(rawDepth, uv);
  vec2 previousUv;
  if (!ProjectToPreviousUv(worldPos, previousUv))
  {
    OutColor = currentAtmosphere;
    return;
  }

  float currentLinearDepth = LinearEyeDistance(rawDepth, uv);
  float reprojectedLinearDepth = LinearEyeDistance(texture(AtmosphereHalfDepth, previousUv).r, previousUv);
  float depthDelta = abs(currentLinearDepth - reprojectedLinearDepth);
  float adaptiveDepthThreshold = max(AtmosphereDepthRejectThreshold, currentLinearDepth * 0.02f);
  float depthConfidence = 1.0f - smoothstep(adaptiveDepthThreshold, adaptiveDepthThreshold * 4.0f, depthDelta);
  if (depthConfidence <= 0.0f)
  {
    OutColor = currentAtmosphere;
    return;
  }

  vec4 historyAtmosphere = texture(AtmosphereHalfHistory, previousUv);
  vec4 boundsMin;
  vec4 boundsMax;
  ComputeNeighborhoodBounds(uv, boundsMin, boundsMax);
  vec4 clippedHistory = ClipToAABB(historyAtmosphere, boundsMin, boundsMax);
  float historyWeight = clamp(AtmosphereTemporalAlpha, 0.0f, 0.98f) * depthConfidence;
  OutColor = mix(currentAtmosphere, clippedHistory, historyWeight);
}
