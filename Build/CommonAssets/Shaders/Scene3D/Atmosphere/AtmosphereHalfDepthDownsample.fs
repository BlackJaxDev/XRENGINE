#version 450

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out float OutDepth;
layout(location = 0) in vec3 FragPos;

uniform sampler2D DepthView;

void main()
{
  vec2 ndc = FragPos.xy;
  if (ndc.x > 1.0f || ndc.y > 1.0f)
    discard;

  vec2 uv = XRENGINE_ClipXYToScreenUV(ndc);
  OutDepth = texture(DepthView, uv).r;
}
