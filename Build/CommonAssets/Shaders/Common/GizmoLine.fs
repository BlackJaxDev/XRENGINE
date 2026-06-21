#version 460

layout(location = 0) out vec4 OutColor;
layout(location = 0) flat in vec4 GizmoLineColor;
layout(location = 1) noperspective in float GizmoLineDistancePixels;
layout(location = 2) flat in float GizmoLineHalfWidthPixels;

void main()
{
    float distancePixels = abs(GizmoLineDistancePixels);
    float aaWidth = max(fwidth(GizmoLineDistancePixels) * 1.1f, 0.9f);
    float edgeAlpha = 1.0f - smoothstep(GizmoLineHalfWidthPixels, GizmoLineHalfWidthPixels + aaWidth, distancePixels);
    float alpha = GizmoLineColor.a * edgeAlpha;
    if (alpha <= 0.001f)
        discard;

    OutColor = vec4(GizmoLineColor.rgb * alpha, alpha);
}
