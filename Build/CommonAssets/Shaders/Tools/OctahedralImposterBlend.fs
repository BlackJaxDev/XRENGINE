// Octahedral impostor sheet composition with multi-view blending
// Captured view set includes 6 axis-aligned, 12 mid-axis, and 8 elevated diagonals (26 total).

#version 450 core

layout(location = 0) in vec2 TexCoord;
layout(location = 0) out vec4 FragColor;

const int VIEW_COUNT = 26;
const float INV_SQRT2 = 0.7071067811865476;
const float INV_SQRT3 = 0.5773502691896258;
const vec3 WORLD_UP = vec3(0.0, 1.0, 0.0);
const vec3 WORLD_ORTHO_UP = vec3(0.0, 0.0, 1.0);

layout(binding = 0) uniform sampler2D ViewSamples[VIEW_COUNT];

const vec3 captureDirections[VIEW_COUNT] = vec3[](
    // Axis-aligned
    vec3(1.0, 0.0, 0.0),
    vec3(-1.0, 0.0, 0.0),
    vec3(0.0, 1.0, 0.0),
    vec3(0.0, -1.0, 0.0),
    vec3(0.0, 0.0, 1.0),
    vec3(0.0, 0.0, -1.0),
    // Edge midpoints (45° between axes)
    vec3(INV_SQRT2,  INV_SQRT2, 0.0),
    vec3(INV_SQRT2, -INV_SQRT2, 0.0),
    vec3(-INV_SQRT2,  INV_SQRT2, 0.0),
    vec3(-INV_SQRT2, -INV_SQRT2, 0.0),
    vec3(INV_SQRT2, 0.0,  INV_SQRT2),
    vec3(INV_SQRT2, 0.0, -INV_SQRT2),
    vec3(-INV_SQRT2, 0.0,  INV_SQRT2),
    vec3(-INV_SQRT2, 0.0, -INV_SQRT2),
    vec3(0.0,  INV_SQRT2,  INV_SQRT2),
    vec3(0.0,  INV_SQRT2, -INV_SQRT2),
    vec3(0.0, -INV_SQRT2,  INV_SQRT2),
    vec3(0.0, -INV_SQRT2, -INV_SQRT2),
    // Elevated diagonals (45° above/below the edge directions)
    vec3(-INV_SQRT3, -INV_SQRT3, -INV_SQRT3),
    vec3(-INV_SQRT3, -INV_SQRT3,  INV_SQRT3),
    vec3(-INV_SQRT3,  INV_SQRT3, -INV_SQRT3),
    vec3(-INV_SQRT3,  INV_SQRT3,  INV_SQRT3),
    vec3( INV_SQRT3, -INV_SQRT3, -INV_SQRT3),
    vec3( INV_SQRT3, -INV_SQRT3,  INV_SQRT3),
    vec3( INV_SQRT3,  INV_SQRT3, -INV_SQRT3),
    vec3( INV_SQRT3,  INV_SQRT3,  INV_SQRT3)
);

vec3 decodeOctahedral(vec2 uv)
{
    vec2 f = uv * 2.0 - 1.0;
    vec3 n = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    if (n.z < 0.0)
        n.xy = (1.0 - abs(n.yx)) * sign(n.xy);
    return normalize(vec3(n.x, n.z, n.y));
}

vec2 projectDirection(vec3 dir, vec3 viewDir)
{
    vec3 upHint = abs(viewDir.y) > 0.999 ? WORLD_ORTHO_UP : WORLD_UP;
    vec3 right = normalize(cross(viewDir, upHint));
    vec3 up = normalize(cross(right, viewDir));
    vec2 projected = vec2(dot(dir, right), dot(dir, up));
    return projected * 0.5 + 0.5;
}

void selectNearestViews(vec3 dir, out ivec3 indices, out vec3 weights)
{
    int bestIndex[3] = int[3](-1, -1, -1);
    float bestWeight[3] = float[3](-1.0, -1.0, -1.0);

    for (int i = 0; i < VIEW_COUNT; ++i)
    {
        float w = max(dot(dir, captureDirections[i]), 0.0);
        if (w > bestWeight[0])
        {
            bestWeight[2] = bestWeight[1];
            bestIndex[2] = bestIndex[1];
            bestWeight[1] = bestWeight[0];
            bestIndex[1] = bestIndex[0];
            bestWeight[0] = w;
            bestIndex[0] = i;
        }
        else if (w > bestWeight[1])
        {
            bestWeight[2] = bestWeight[1];
            bestIndex[2] = bestIndex[1];
            bestWeight[1] = w;
            bestIndex[1] = i;
        }
        else if (w > bestWeight[2])
        {
            bestWeight[2] = w;
            bestIndex[2] = i;
        }
    }

    indices = ivec3(bestIndex[0], bestIndex[1], bestIndex[2]);
    weights = vec3(max(bestWeight[0], 0.0), max(bestWeight[1], 0.0), max(bestWeight[2], 0.0));
}

void main()
{
    vec3 dir = decodeOctahedral(TexCoord);

    ivec3 indices;
    vec3 weights;
    selectNearestViews(dir, indices, weights);

    float weightSum = weights.x + weights.y + weights.z;
    if (weightSum <= 1e-5)
    {
        FragColor = vec4(0.0);
        return;
    }

    vec4 color = vec4(0.0);
    for (int i = 0; i < 3; ++i)
    {
        int idx = indices[i];
        float w = weights[i];
        if (idx < 0 || w <= 0.0)
            continue;

        vec2 uv = clamp(projectDirection(dir, captureDirections[idx]), vec2(0.0), vec2(1.0));
        vec4 sampleColor = texture(ViewSamples[idx], uv);
        color += sampleColor * (w / weightSum);
    }

    FragColor = color;
}
