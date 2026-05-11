#version 450

layout(location = 0) out float OutDepth;
layout(location = 0) in vec3 FragPos;

uniform sampler2D DepthView;

void main()
{
  vec2 ndc = FragPos.xy;
  if (ndc.x > 1.0f || ndc.y > 1.0f)
    discard;

  vec2 uv = ndc * 0.5f + 0.5f;
  OutDepth = texture(DepthView, uv).r;
}
