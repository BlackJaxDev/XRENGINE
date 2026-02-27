// Clips a line segment to the near plane and emits a screen-space quad.
// Requires the following to be defined before including:
//   uniform mat4 InverseViewMatrix;
//   uniform mat4 ProjMatrix;
//   uniform float LineWidth;
//   uniform float ScreenWidth;
//   uniform float ScreenHeight;
//   layout(location = 0) out vec4 MatColor;

const float kEpsilon = 1e-6;

bool ClipLineToNearPlane(inout vec4 a, inout vec4 b)
{
    // OpenGL clip-space near plane: z >= -w  <=>  z + w >= 0
    float da = a.z + a.w;
    float db = b.z + b.w;

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

    MatColor = color;

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
    float halfWidthPixels = LineWidth * (minDim * 0.5);
    vec2 offsetNdc = (perpScreen * halfWidthPixels) * (2.0 / viewport);

    vec4 startOffset = vec4(offsetNdc * start.w, 0.0, 0.0);
    vec4 endOffset   = vec4(offsetNdc * end.w,   0.0, 0.0);

    gl_Position = start + startOffset;
    EmitVertex();

    gl_Position = start - startOffset;
    EmitVertex();

    gl_Position = end + endOffset;
    EmitVertex();

    gl_Position = end - endOffset;
    EmitVertex();

    EndPrimitive();
}
