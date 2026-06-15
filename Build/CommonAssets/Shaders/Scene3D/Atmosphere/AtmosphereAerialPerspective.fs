#version 450

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D AtmosphereHalfDepth;

uniform vec3 CameraPosition;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform float RenderTime;
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

vec3 WorldPosFromDepthRaw(float rawDepth, vec2 uv)
{
  vec4 clipSpacePosition = vec4(XRENGINE_FramebufferTextureUVToClipXY(uv), AtmosphereDepthToClipZ(rawDepth), 1.0f);
  vec4 viewSpacePosition = InverseProjMatrix * clipSpacePosition;
  float safeW = max(abs(viewSpacePosition.w), 1e-5f);
  viewSpacePosition /= safeW * sign(viewSpacePosition.w == 0.0f ? 1.0f : viewSpacePosition.w);
  return (InverseViewMatrix * viewSpacePosition).xyz;
}

float InterleavedGradientNoise(vec2 pixelCoord)
{
  return fract(52.9829189f * fract(dot(pixelCoord, vec2(0.06711056f, 0.00583715f))));
}

void main()
{
  vec2 ndc = FragPos.xy;
  if (ndc.x > 1.0f || ndc.y > 1.0f)
    discard;

  if (!Atmosphere.Enabled || !Atmosphere.AerialPerspective || Atmosphere.MaxDistance <= 0.0f)
  {
    OutColor = vec4(0.0f, 0.0f, 0.0f, 1.0f);
    return;
  }

  vec2 uv = XRENGINE_ClipXYToScreenUV(ndc);
  float rawDepth = texture(AtmosphereHalfDepth, uv).r;
  float resolvedDepth = ResolveDepth(rawDepth);
  if (resolvedDepth >= 0.999999f)
  {
    OutColor = vec4(0.0f, 0.0f, 0.0f, 1.0f);
    return;
  }

  vec3 surfacePos = WorldPosFromDepthRaw(rawDepth, uv);
  vec3 rayVector = surfacePos - CameraPosition;
  float rayLength = length(rayVector);
  if (rayLength <= 1e-5f)
  {
    OutColor = vec4(0.0f, 0.0f, 0.0f, 1.0f);
    return;
  }

  vec3 rayDir = rayVector / rayLength;
  vec2 noiseCoord = XRENGINE_ScreenNoiseCoord(gl_FragCoord.xy, vec2(0.0), vec2(textureSize(AtmosphereHalfDepth, 0)));
  float jitter = (InterleavedGradientNoise(noiseCoord + RenderTime) - 0.5f) * Atmosphere.JitterStrength;
  float maxDistance = min(rayLength, Atmosphere.MaxDistance + jitter);
  vec4 atmosphere = XRENGINE_Atmosphere_ComputeScattering(CameraPosition, rayDir, maxDistance, false);
  OutColor = XRENGINE_Atmosphere_DebugOutput(CameraPosition, rayDir, maxDistance, atmosphere);
}
