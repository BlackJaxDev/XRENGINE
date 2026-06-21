#version 460

layout(lines) in;
layout(triangle_strip, max_vertices = 4) out;

uniform vec4 MatColor;
uniform float LineWidth;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform int ClipDepthRange;

layout(location = 0) flat out vec4 GizmoLineColor;
layout(location = 1) noperspective out float GizmoLineDistancePixels;
layout(location = 2) flat out float GizmoLineHalfWidthPixels;

const float kEpsilon = 1e-6;

bool ClipLineToNearPlane(inout vec4 a, inout vec4 b)
{
    float da = ClipDepthRange == 1 ? a.z + a.w : a.z;
    float db = ClipDepthRange == 1 ? b.z + b.w : b.z;

    if (da < 0.0 && db < 0.0)
        return false;

    if (da < 0.0 || db < 0.0)
    {
        float denom = da - db;
        float t = abs(denom) < kEpsilon ? 0.0 : da / denom;
        vec4 p = mix(a, b, clamp(t, 0.0, 1.0));
        if (da < 0.0)
            a = p;
        else
            b = p;
    }

    return a.w > kEpsilon && b.w > kEpsilon;
}

void EmitLineVertex(vec4 position, float distancePixels, float halfWidthPixels)
{
    GizmoLineColor = MatColor;
    GizmoLineHalfWidthPixels = halfWidthPixels;
    GizmoLineDistancePixels = distancePixels;
    gl_Position = position;
    EmitVertex();
}

void main()
{
    vec4 start = gl_in[0].gl_Position;
    vec4 end = gl_in[1].gl_Position;
    if (!ClipLineToNearPlane(start, end))
        return;

    float w = max(1.0, ScreenWidth);
    float h = max(1.0, ScreenHeight);
    vec2 viewport = vec2(w, h);
    vec2 halfViewport = viewport * 0.5;

    vec2 screenStart = (start.xy / start.w) * halfViewport;
    vec2 screenEnd = (end.xy / end.w) * halfViewport;
    vec2 delta = screenEnd - screenStart;
    float len2 = dot(delta, delta);
    vec2 dir = len2 < kEpsilon ? vec2(1.0, 0.0) : delta * inversesqrt(len2);
    vec2 perpScreen = vec2(-dir.y, dir.x);

    float halfWidthPixels = max(0.45, LineWidth * 0.5);
    float aaPixels = 0.85;
    float rasterHalfWidthPixels = halfWidthPixels + aaPixels;
    vec2 offsetNdc = (perpScreen * rasterHalfWidthPixels) * (2.0 / viewport);
    vec4 startOffset = vec4(offsetNdc * start.w, 0.0, 0.0);
    vec4 endOffset = vec4(offsetNdc * end.w, 0.0, 0.0);

    EmitLineVertex(start + startOffset, rasterHalfWidthPixels, halfWidthPixels);
    EmitLineVertex(start - startOffset, -rasterHalfWidthPixels, halfWidthPixels);
    EmitLineVertex(end + endOffset, rasterHalfWidthPixels, halfWidthPixels);
    EmitLineVertex(end - endOffset, -rasterHalfWidthPixels, halfWidthPixels);
    EndPrimitive();
}
