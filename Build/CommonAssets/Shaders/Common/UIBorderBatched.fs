#version 460

layout(location = 4) in vec2 FragUV0;
layout(location = 5) flat in vec4 FillColor;
layout(location = 6) flat in vec4 OutlineColor;
layout(location = 7) flat in vec3 BorderSize;
layout(location = 0) out vec4 OutColor;

void main()
{
    vec2 widthUv = vec2(BorderSize.z) / max(BorderSize.xy, vec2(0.0001));
    float edge = max(max(step(FragUV0.x, widthUv.x), step(1.0 - widthUv.x, FragUV0.x)),
        max(step(FragUV0.y, widthUv.y), step(1.0 - widthUv.y, FragUV0.y)));
    OutColor = mix(FillColor, OutlineColor, edge);
}
