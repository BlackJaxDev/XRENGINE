#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 1) in vec3 FragWorldDir;

uniform vec3 CameraPosition;

#include "AtmosphereCommon.glsl"

void main()
{
  if (!Atmosphere.Enabled || !Atmosphere.RenderSky)
    discard;

  vec3 rayDir = normalize(FragWorldDir);
  vec4 atmosphere = XRENGINE_Atmosphere_ComputeScattering(
    CameraPosition,
    rayDir,
    Atmosphere.OuterRadius * 2.0f,
    true);
  atmosphere = XRENGINE_Atmosphere_DebugOutput(CameraPosition, rayDir, Atmosphere.OuterRadius * 2.0f, atmosphere);

  if (atmosphere.a >= 0.9999f && all(lessThanEqual(abs(atmosphere.rgb), vec3(1e-6f))))
    discard;

  OutColor = vec4(max(atmosphere.rgb, vec3(0.0f)), 1.0f);
}
