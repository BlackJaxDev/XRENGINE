#version 460

layout(lines) in;
layout(triangle_strip, max_vertices = 4) out;

uniform vec4 MatColor;
uniform float LineWidth;
uniform float ScreenWidth;
uniform float ScreenHeight;

layout(location = 0) flat out vec4 GizmoLineColor;
layout(location = 1) noperspective out float GizmoLineDistancePixels;
layout(location = 2) flat out float GizmoLineHalfWidthPixels;

const float kEpsilon = 1e-6;

bool ClipLineToNearPlane(inout vec4 a, inout vec4 b)
{
#ifdef XRENGINE_VULKAN
    float da = a.z;
    float db = b.z;
#else
    float da = a.z + a.w;
    float db = b.z + b.w;
#endif

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

void EmitLineVertex(vec4 position, float distancePixels)
{
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

    GizmoLineColor = MatColor;

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
    GizmoLineHalfWidthPixels = halfWidthPixels;

    vec2 offsetNdc = (perpScreen * rasterHalfWidthPixels) * (2.0 / viewport);
    vec4 startOffset = vec4(offsetNdc * start.w, 0.0, 0.0);
    vec4 endOffset = vec4(offsetNdc * end.w, 0.0, 0.0);

    EmitLineVertex(start + startOffset, rasterHalfWidthPixels);
    EmitLineVertex(start - startOffset, -rasterHalfWidthPixels);
    EmitLineVertex(end + endOffset, rasterHalfWidthPixels);
    EmitLineVertex(end - endOffset, -rasterHalfWidthPixels);
    EndPrimitive();
}
