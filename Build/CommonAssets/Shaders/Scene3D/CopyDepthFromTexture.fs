#version 450 core

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;
layout(binding = 0) uniform sampler2D DepthView;

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x > 1.0f || clipXY.y > 1.0f)
        discard;

    vec2 uv = clipXY * 0.5f + 0.5f;
    float depth = texture(DepthView, uv).r;
    gl_FragDepth = depth;
    OutColor = vec4(0.0f);
}
