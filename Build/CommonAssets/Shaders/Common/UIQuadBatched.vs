#version 460

layout (location = 0) in vec3 Position;
layout (location = 1) in vec3 Normal;
layout (location = 2) in vec2 TexCoord0;

// Per-instance data stored in SSBOs.
// Model matrices stored as 4 consecutive vec4 rows per instance.
layout(std430, binding = 0) buffer QuadTransformBuffer
{
    vec4 QuadTransforms[]; // 4 vec4s per instance (row-major mat4)
};

layout(std430, binding = 1) buffer QuadColorBuffer
{
    vec4 QuadColors[];
};

layout(std430, binding = 2) buffer QuadBoundsBuffer
{
    vec4 QuadBounds[]; // (x, y, w, h) per instance
};

// Per-instance CSS-inspired box style (7 vec4s per instance):
// [0] CornerRadii (tl, tr, br, bl)
// [1] StyleMeta (cornerExponent, borderWidth, borderHighlightBoost, flags)
// [2] BorderColor (r, g, b, a)
// [3] GradientBottomColor (r, g, b, a)
// [4] ShadowColor (r, g, b, a)
// [5] ShadowParams (offsetX, offsetY, blurRadius, spread)
// [6] BackdropBlurParams (blurRadiusPx, blurBlend, vibrancySaturation, vibrancyBrightness)
layout(std430, binding = 4) buffer QuadStyleBuffer
{
    vec4 QuadStyles[];
};

// Painter-order draw index to stable per-quad storage slot.
layout(std430, binding = 5) buffer QuadDrawIndexBuffer
{
    uint QuadDrawIndices[];
};

uniform mat4 ViewProjectionMatrix_VTX;

layout (location = 0) out vec3 FragPos;
layout (location = 1) out vec3 FragNorm;
layout (location = 4) out vec2 FragUV0;
layout (location = 5) flat out vec4 InstanceColor;
layout (location = 6) flat out vec4 InstanceBounds;
layout (location = 7) out vec2 FragLocalPos;
layout (location = 8) flat out vec4 InstanceCornerRadii;
layout (location = 9) flat out vec4 InstanceStyleMeta;
layout (location = 10) flat out vec4 InstanceBorderColor;
layout (location = 11) flat out vec4 InstanceGradientBottom;
layout (location = 12) flat out vec4 InstanceShadowColor;
layout (location = 13) flat out vec4 InstanceShadowParams;
layout (location = 14) flat out vec4 InstanceBlurParams;

const int StyleStride = 7;
const uint FlagDropShadow = 8u;

mat4 getModelMatrix(int instanceID)
{
    int base4 = instanceID * 4;
    // C# Matrix4x4 is row-major with row-vector semantics.
    // GLSL mat4 uses column-major storage with column-vector semantics.
    // Build columns directly from the serialized rows to apply the required transpose.
    vec4 row0 = QuadTransforms[base4 + 0];
    vec4 row1 = QuadTransforms[base4 + 1];
    vec4 row2 = QuadTransforms[base4 + 2];
    vec4 row3 = QuadTransforms[base4 + 3];
    return mat4(row0, row1, row2, row3);
}

void main()
{
    // Use gl_InstanceID for OpenGL compatibility; Vulkan fixup auto-converts to gl_InstanceIndex.
    int id = int(QuadDrawIndices[gl_InstanceID]);

    mat4 modelMatrix = getModelMatrix(id);
    mat4 mvpMatrix = ViewProjectionMatrix_VTX * modelMatrix;

    vec4 bounds = QuadBounds[id];
    int styleBase = id * StyleStride;
    vec4 cornerRadii = QuadStyles[styleBase + 0];
    vec4 styleMeta = QuadStyles[styleBase + 1];
    vec4 borderColor = QuadStyles[styleBase + 2];
    vec4 gradientBottom = QuadStyles[styleBase + 3];
    vec4 shadowColor = QuadStyles[styleBase + 4];
    vec4 shadowParams = QuadStyles[styleBase + 5];
    vec4 blurParams = QuadStyles[styleBase + 6];

    vec2 boxSize = max(bounds.zw, vec2(1e-4));
    vec2 unitPos = Position.xy;
    uint flags = uint(styleMeta.w + 0.5);

    if ((flags & FlagDropShadow) != 0u && shadowColor.a > 0.0001 && (shadowParams.z > 0.0 || shadowParams.w > 0.0))
    {
        float margin = max(shadowParams.z * 2.5 + max(shadowParams.w, 0.0), 0.0);
        // Positive CSS shadow offsetY shifts downward (-Y in bottom-left UI coordinates).
        vec2 padMin = vec2(margin + max(-shadowParams.x, 0.0), margin + max(shadowParams.y, 0.0));
        vec2 padMax = vec2(margin + max(shadowParams.x, 0.0), margin + max(-shadowParams.y, 0.0));
        unitPos = mix(-padMin / boxSize, vec2(1.0) + padMax / boxSize, Position.xy);
    }

    vec4 localVertex = vec4(unitPos, Position.z, 1.0);

    FragPos = (modelMatrix * localVertex).xyz;
    FragNorm = Normal;
    FragUV0 = TexCoord0;
    InstanceColor = QuadColors[id];
    InstanceBounds = bounds;
    FragLocalPos = unitPos * boxSize;
    InstanceCornerRadii = cornerRadii;
    InstanceStyleMeta = styleMeta;
    InstanceBorderColor = borderColor;
    InstanceGradientBottom = gradientBottom;
    InstanceShadowColor = shadowColor;
    InstanceShadowParams = shadowParams;
    InstanceBlurParams = blurParams;

    gl_Position = mvpMatrix * localVertex;
}
