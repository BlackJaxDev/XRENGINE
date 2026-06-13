#version 450

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D AtmosphereHalfTemporal;
uniform sampler2D AtmosphereHalfDepth;
uniform sampler2D DepthView;

uniform mat4 InverseProjMatrix;
uniform int DepthMode;

#ifndef XRENGINE_CLIP_DEPTH_RANGE_UNIFORM
#define XRENGINE_CLIP_DEPTH_RANGE_UNIFORM
uniform int ClipDepthRange;
#endif

#include "AtmosphereCommon.glsl"

float ResolveDepth(float depth)
{
  return DepthMode == 1 ? (1.0f - depth) : depth;
}

float AtmosphereDepthToClipZ(float depth)
{
  return ClipDepthRange == 1 ? depth * 2.0f - 1.0f : depth;
}

float LinearEyeDistance(float rawDepth, vec2 uv)
{
  vec4 clip = vec4(uv * 2.0f - 1.0f, AtmosphereDepthToClipZ(rawDepth), 1.0f);
  vec4 view = InverseProjMatrix * clip;
  float w = max(abs(view.w), 1e-5f);
  return abs(view.z / w);
}

bool IsNeutralAtmosphere(vec4 value)
{
  return value.a >= 0.9999f && all(lessThanEqual(abs(value.rgb), vec3(1e-5f)));
}

void main()
{
  vec2 ndc = FragPos.xy;
  if (ndc.x > 1.0f || ndc.y > 1.0f)
    discard;

  if (!Atmosphere.Enabled || !Atmosphere.AerialPerspective)
  {
    OutColor = vec4(0.0f, 0.0f, 0.0f, 1.0f);
    return;
  }

  vec2 uv = XRENGINE_ClipXYToScreenUV(ndc);
  float fullRawDepth = texture(DepthView, uv).r;
  if (ResolveDepth(fullRawDepth) >= 0.999999f)
  {
    OutColor = vec4(0.0f, 0.0f, 0.0f, 1.0f);
    return;
  }

  ivec2 halfSize = textureSize(AtmosphereHalfTemporal, 0);
  vec2 halfTexelUv = uv * vec2(halfSize) - 0.5f;
  ivec2 baseTexel = ivec2(floor(halfTexelUv));
  vec2 fracPart = fract(halfTexelUv);
  float fullDepthLinear = LinearEyeDistance(fullRawDepth, uv);
  vec4 sum = vec4(0.0f);
  float totalWeight = 0.0f;
  vec4 closest = vec4(0.0f, 0.0f, 0.0f, 1.0f);
  float closestDepthDelta = 1.0e20f;

  for (int y = 0; y <= 1; ++y)
  {
    for (int x = 0; x <= 1; ++x)
    {
      ivec2 tapTexel = clamp(baseTexel + ivec2(x, y), ivec2(0), halfSize - ivec2(1));
      vec2 tapUv = (vec2(tapTexel) + 0.5f) / vec2(halfSize);
      vec4 tapAtmosphere = texture(AtmosphereHalfTemporal, tapUv);
      float tapRawDepth = texelFetch(AtmosphereHalfDepth, tapTexel, 0).r;
      float tapDepthLinear = LinearEyeDistance(tapRawDepth, tapUv);
      float depthDelta = abs(tapDepthLinear - fullDepthLinear);
      float spatialWeight = ((x == 0) ? (1.0f - fracPart.x) : fracPart.x)
        * ((y == 0) ? (1.0f - fracPart.y) : fracPart.y);
      float depthWeight = exp(-(depthDelta * depthDelta) / max(fullDepthLinear * fullDepthLinear * 0.0004f, 1e-4f));
      float weight = spatialWeight * depthWeight;
      sum += tapAtmosphere * weight;
      totalWeight += weight;

      if (depthDelta < closestDepthDelta)
      {
        closestDepthDelta = depthDelta;
        closest = tapAtmosphere;
      }
    }
  }

  vec4 atmosphere = totalWeight > 1e-5f ? sum / totalWeight : closest;
  OutColor = IsNeutralAtmosphere(atmosphere) ? vec4(0.0f, 0.0f, 0.0f, 1.0f) : atmosphere;
}
