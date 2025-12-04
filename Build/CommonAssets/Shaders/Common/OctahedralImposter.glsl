#ifndef XR_OCTAHEDRAL_IMPOSTER_GLSL
#define XR_OCTAHEDRAL_IMPOSTER_GLSL

const int XR_OCTA_VIEW_COUNT = 26;
const float XR_OCTA_WEIGHT_EPSILON = 1e-5;

const float XR_OCTA_INV_SQRT2 = 0.7071067811865476;
const float XR_OCTA_INV_SQRT3 = 0.5773502691896258;

const vec3 XR_OCTA_CAPTURE_DIRECTIONS[XR_OCTA_VIEW_COUNT] = vec3[](
    vec3(1.0, 0.0, 0.0),
    vec3(-1.0, 0.0, 0.0),
    vec3(0.0, 1.0, 0.0),
    vec3(0.0, -1.0, 0.0),
    vec3(0.0, 0.0, 1.0),
    vec3(0.0, 0.0, -1.0),
    vec3( XR_OCTA_INV_SQRT2,  XR_OCTA_INV_SQRT2, 0.0),
    vec3( XR_OCTA_INV_SQRT2, -XR_OCTA_INV_SQRT2, 0.0),
    vec3(-XR_OCTA_INV_SQRT2,  XR_OCTA_INV_SQRT2, 0.0),
    vec3(-XR_OCTA_INV_SQRT2, -XR_OCTA_INV_SQRT2, 0.0),
    vec3( XR_OCTA_INV_SQRT2, 0.0,  XR_OCTA_INV_SQRT2),
    vec3( XR_OCTA_INV_SQRT2, 0.0, -XR_OCTA_INV_SQRT2),
    vec3(-XR_OCTA_INV_SQRT2, 0.0,  XR_OCTA_INV_SQRT2),
    vec3(-XR_OCTA_INV_SQRT2, 0.0, -XR_OCTA_INV_SQRT2),
    vec3(0.0,  XR_OCTA_INV_SQRT2,  XR_OCTA_INV_SQRT2),
    vec3(0.0,  XR_OCTA_INV_SQRT2, -XR_OCTA_INV_SQRT2),
    vec3(0.0, -XR_OCTA_INV_SQRT2,  XR_OCTA_INV_SQRT2),
    vec3(0.0, -XR_OCTA_INV_SQRT2, -XR_OCTA_INV_SQRT2),
    vec3(-XR_OCTA_INV_SQRT3, -XR_OCTA_INV_SQRT3, -XR_OCTA_INV_SQRT3),
    vec3(-XR_OCTA_INV_SQRT3, -XR_OCTA_INV_SQRT3,  XR_OCTA_INV_SQRT3),
    vec3(-XR_OCTA_INV_SQRT3,  XR_OCTA_INV_SQRT3, -XR_OCTA_INV_SQRT3),
    vec3(-XR_OCTA_INV_SQRT3,  XR_OCTA_INV_SQRT3,  XR_OCTA_INV_SQRT3),
    vec3( XR_OCTA_INV_SQRT3, -XR_OCTA_INV_SQRT3, -XR_OCTA_INV_SQRT3),
    vec3( XR_OCTA_INV_SQRT3, -XR_OCTA_INV_SQRT3,  XR_OCTA_INV_SQRT3),
    vec3( XR_OCTA_INV_SQRT3,  XR_OCTA_INV_SQRT3, -XR_OCTA_INV_SQRT3),
    vec3( XR_OCTA_INV_SQRT3,  XR_OCTA_INV_SQRT3,  XR_OCTA_INV_SQRT3)
);

vec3 XR_OctahedralViewDirection(vec3 cameraPosition, vec3 imposterCenter)
{
    return normalize(cameraPosition - imposterCenter);
}

vec3 XR_OctahedralViewDirection(mat4 modelMatrix, mat4 inverseViewMatrix)
{
    vec3 cameraPosition = inverseViewMatrix[3].xyz;
    vec3 center = modelMatrix[3].xyz;
    return XR_OctahedralViewDirection(cameraPosition, center);
}

void XR_OctahedralSelectNearest(vec3 dir, out ivec3 indices, out vec3 weights)
{
    float w0 = -1.0; int i0 = -1;
    float w1 = -1.0; int i1 = -1;
    float w2 = -1.0; int i2 = -1;

    for (int i = 0; i < XR_OCTA_VIEW_COUNT; ++i)
    {
        float w = max(dot(dir, XR_OCTA_CAPTURE_DIRECTIONS[i]), 0.0);
        if (w > w0)
        {
            w2 = w1; i2 = i1;
            w1 = w0; i1 = i0;
            w0 = w;  i0 = i;
        }
        else if (w > w1)
        {
            w2 = w1; i2 = i1;
            w1 = w;  i1 = i;
        }
        else if (w > w2)
        {
            w2 = w;  i2 = i;
        }
    }

    indices = ivec3(i0, i1, i2);
    weights = vec3(max(w0, 0.0), max(w1, 0.0), max(w2, 0.0));
}

vec4 XR_OctahedralSampleLayer(sampler2DArray atlas, vec2 uv, int layer)
{
    return texture(atlas, vec3(uv, float(layer)));
}

vec4 XR_OctahedralBlendLayers(sampler2DArray atlas, vec2 uv, ivec3 indices, vec3 weights)
{
    float weightSum = weights.x + weights.y + weights.z;
    if (weightSum <= XR_OCTA_WEIGHT_EPSILON)
    {
        int fallbackLayer = max(indices.x, 0);
        return XR_OctahedralSampleLayer(atlas, uv, fallbackLayer);
    }

    vec4 blended = vec4(0.0);
    for (int i = 0; i < 3; ++i)
    {
        int layer = indices[i];
        float w = weights[i];
        if (layer < 0 || w <= 0.0)
            continue;
        blended += XR_OctahedralSampleLayer(atlas, uv, layer) * (w / weightSum);
    }

    return blended;
}

vec4 XR_SampleOctahedralImposter(sampler2DArray atlas, vec2 uv, vec3 viewDir)
{
    ivec3 indices;
    vec3 weights;
    XR_OctahedralSelectNearest(viewDir, indices, weights);
    return XR_OctahedralBlendLayers(atlas, uv, indices, weights);
}

#endif // XR_OCTAHEDRAL_IMPOSTER_GLSL
