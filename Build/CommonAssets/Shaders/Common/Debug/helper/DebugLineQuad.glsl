// Clips a line segment to the near plane and emits a screen-space quad.
// Requires the following to be defined before including:
//   uniform float LineWidth;
//   uniform float ScreenWidth;
//   uniform float ScreenHeight;
//   layout(location = 0) flat out vec4 LineMatColor;
//   layout(location = 1) noperspective out float LineEdgeCoord;
//   layout(location = 2) flat out float LineHalfWidthPixels;

const float kEpsilon = 1e-6;

bool ClipLineToNearPlane(inout vec4 a, inout vec4 b)
{
#ifdef XRENGINE_VULKAN
    // Vulkan/D3D clip-space near plane: z >= 0.
    float da = a.z;
    float db = b.z;
#else
    // OpenGL clip-space near plane: z >= -w  <=>  z + w >= 0.
    float da = a.z + a.w;
    float db = b.z + b.w;
#endif

    if (da < 0.0 && db < 0.0)
        return false;

    if (da < 0.0 || db < 0.0)
    {
        // Intersect segment with the near plane in clip space.
        float denom = (da - db);
        float t = (abs(denom) < kEpsilon) ? 0.0 : (da / denom);
        t = clamp(t, 0.0, 1.0);
        vec4 p = mix(a, b, t);
        if (da < 0.0)
            a = p;
        else
            b = p;
    }

    // Guard against behind-camera / invalid perspective divide.
    if (a.w <= kEpsilon || b.w <= kEpsilon)
        return false;

    return true;
}

void EmitLineQuad(vec4 start, vec4 end, vec4 color)
{
    // Robustly handle segments that cross the near plane or go behind the camera.
    if (!ClipLineToNearPlane(start, end))
        return;

    start = XRENGINE_DebugOutputPosition(start);
    end = XRENGINE_DebugOutputPosition(end);

    vec2 ndcStart = start.xy / start.w;
    vec2 ndcEnd   = end.xy   / end.w;

    // Compute perpendicular in pixel space to keep thickness constant regardless of aspect ratio.
    float w = max(1.0, ScreenWidth);
    float h = max(1.0, ScreenHeight);
    vec2 viewport     = vec2(w, h);
    vec2 halfViewport = viewport * 0.5;
    vec2 screenStart  = ndcStart * halfViewport;
    vec2 screenEnd    = ndcEnd   * halfViewport;

    vec2 delta = screenEnd - screenStart;
    float len2 = dot(delta, delta);
    vec2 dir = (len2 < kEpsilon) ? vec2(1.0, 0.0) : (delta * inversesqrt(len2));
    vec2 perpScreen = vec2(-dir.y, dir.x);

    // Preserve existing LineWidth semantics by interpreting it as NDC width relative to the *min* screen dimension.
    float minDim = min(w, h);
    float halfWidthPixels = max(0.5, LineWidth * (minDim * 0.5));
    float rasterHalfWidthPixels = halfWidthPixels + 2.0;

    vec2 offsetNdc = (perpScreen * rasterHalfWidthPixels) * (2.0 / viewport);

    vec4 startOffset = vec4(offsetNdc * start.w, 0.0, 0.0);
    vec4 endOffset   = vec4(offsetNdc * end.w,   0.0, 0.0);

    vec4 startPlus = start + startOffset;
    vec4 startMinus = start - startOffset;
    vec4 endPlus = end + endOffset;
    vec4 endMinus = end - endOffset;

    LineMatColor = color;
    LineHalfWidthPixels = halfWidthPixels;
    LineEdgeCoord = rasterHalfWidthPixels;
    gl_Position = startPlus;
    EmitVertex();

    LineMatColor = color;
    LineHalfWidthPixels = halfWidthPixels;
    LineEdgeCoord = -rasterHalfWidthPixels;
    gl_Position = startMinus;
    EmitVertex();

    LineMatColor = color;
    LineHalfWidthPixels = halfWidthPixels;
    LineEdgeCoord = rasterHalfWidthPixels;
    gl_Position = endPlus;
    EmitVertex();

    LineMatColor = color;
    LineHalfWidthPixels = halfWidthPixels;
    LineEdgeCoord = -rasterHalfWidthPixels;
    gl_Position = endMinus;
    EmitVertex();

    EndPrimitive();
}
