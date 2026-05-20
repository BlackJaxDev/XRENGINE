#version 460

layout (location = 0) out vec4 OutColor;
layout (location = 0) flat in vec4 LineMatColor;
layout (location = 1) noperspective in float LineEdgeCoord;
layout (location = 2) flat in float LineHalfWidthPixels;

void main()
{
    float distancePixels = abs(LineEdgeCoord);
    float aaWidth = max(fwidth(LineEdgeCoord) * 1.25f, 0.75f);
    float edgeAlpha = 1.0f - smoothstep(LineHalfWidthPixels, LineHalfWidthPixels + aaWidth, distancePixels);
    float alpha = LineMatColor.a * edgeAlpha;
    if (alpha <= 0.001f)
        discard;

    OutColor = vec4(LineMatColor.rgb, alpha);
}