#version 460

layout(location = 0) out vec4 OutColor;
layout(location = 0) flat in vec4 ArrowColor;
layout(location = 1) noperspective in vec3 ArrowBarycentric;

void main()
{
    float edgeDistance = min(min(ArrowBarycentric.x, ArrowBarycentric.y), ArrowBarycentric.z);
    float aaWidth = max(fwidth(edgeDistance) * 1.25f, 1e-4f);
    float edgeAlpha = smoothstep(0.0f, aaWidth, edgeDistance);
    float alpha = ArrowColor.a * edgeAlpha;
    if (alpha <= 0.001f)
        discard;

    OutColor = vec4(ArrowColor.rgb * alpha, alpha);
}
