#version 460

layout(lines) in;
layout(triangle_strip, max_vertices = 3) out;

uniform vec4 MatColor;
uniform float ArrowHeadLengthPixels;
uniform float ArrowHeadHalfWidthPixels;
uniform float ScreenWidth;
uniform float ScreenHeight;

layout(location = 0) flat out vec4 ArrowColor;
layout(location = 1) noperspective out vec3 ArrowBarycentric;

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

vec4 ClipFromScreen(vec2 screen, vec4 referenceClip, vec2 halfViewport)
{
    vec2 ndc = screen / halfViewport;
    return vec4(ndc * referenceClip.w, referenceClip.z, referenceClip.w);
}

void EmitArrowVertex(vec4 position, vec3 barycentric)
{
    ArrowColor = MatColor;
    ArrowBarycentric = barycentric;
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
    vec2 halfViewport = vec2(w, h) * 0.5;

    vec2 screenStart = (start.xy / start.w) * halfViewport;
    vec2 screenEnd = (end.xy / end.w) * halfViewport;
    vec2 delta = screenEnd - screenStart;
    float len2 = dot(delta, delta);
    vec2 dir = len2 < kEpsilon ? vec2(1.0, 0.0) : delta * inversesqrt(len2);
    vec2 perp = vec2(-dir.y, dir.x);

    float lengthPixels = max(1.0, ArrowHeadLengthPixels);
    float halfWidthPixels = max(1.0, ArrowHeadHalfWidthPixels);
    vec2 baseCenter = screenEnd - dir * lengthPixels;

    vec4 tip = ClipFromScreen(screenEnd, end, halfViewport);
    vec4 baseA = ClipFromScreen(baseCenter + perp * halfWidthPixels, end, halfViewport);
    vec4 baseB = ClipFromScreen(baseCenter - perp * halfWidthPixels, end, halfViewport);

    EmitArrowVertex(tip, vec3(1.0, 0.0, 0.0));
    EmitArrowVertex(baseA, vec3(0.0, 1.0, 0.0));
    EmitArrowVertex(baseB, vec3(0.0, 0.0, 1.0));
    EndPrimitive();
}
