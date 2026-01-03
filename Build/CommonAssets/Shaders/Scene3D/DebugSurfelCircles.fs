#version 450 core

// Surfel Debug Visualization: Renders surfels as colored circles on the scene
layout(location = 0) out vec4 OutColor;

uniform sampler2D DepthView;      // binding 0
uniform sampler2D Normal;         // binding 1
uniform sampler2D AlbedoOpacity;  // binding 2
uniform usampler2D TransformId;   // binding 3
uniform sampler2D HDRSceneTex;    // binding 4

uniform float ScreenWidth;
uniform float ScreenHeight;
uniform mat4 InvProjectionMatrix;
uniform mat4 CameraToWorldMatrix;

// Surfel buffer - must match VPRC_SurfelGIPass layout
struct Surfel
{
    vec4 posRadius; // xyz=localPos, w=worldRadius
    vec4 normal;    // xyz=localNormal
    vec4 albedo;
    uvec4 meta;     // x=lastUsedFrame, y=active, z=transformId
};

layout(std430, binding = 0) buffer SurfelBuffer
{
    Surfel surfels[];
};

layout(std430, binding = 1) buffer CounterBuffer
{
    int stackTop;
    int pad0;
    int pad1;
    int pad2;
};

// Grid information
uniform vec3 gridOrigin;
uniform float cellSize;
uniform uvec3 gridDim;
uniform uint maxPerCell;
uniform uint maxSurfels;

layout(std430, binding = 3) buffer GridCounts
{
    uint counts[];
};

layout(std430, binding = 4) buffer GridIndices
{
    uint indices[];
};

// GPU commands buffer for world matrix reconstruction
layout(std430, binding = 5) buffer CulledCommandsBuffer { float culled[]; };
uniform bool hasCulledCommands;
uniform uint culledFloatCount;
uniform uint culledCommandFloats;

vec3 HashColor(uint id)
{
    // Cheap integer hash -> RGB in [0,1]
    uint x = id;
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;

    return vec3(
        float((x >> 0) & 255u),
        float((x >> 8) & 255u),
        float((x >> 16) & 255u)) / 255.0;
}

vec3 ReconstructWorldPosition(vec2 uv, float depth)
{
    vec4 clip = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 view = InvProjectionMatrix * clip;
    view /= max(view.w, 1e-6);
    vec4 world = CameraToWorldMatrix * view;
    return world.xyz;
}

uint CellIndex(uvec3 c)
{
    return c.x + gridDim.x * (c.y + gridDim.y * c.z);
}

bool WorldToCell(vec3 p, out uint cell)
{
    vec3 rel = (p - gridOrigin) / max(cellSize, 1e-6);
    ivec3 ci = ivec3(floor(rel));
    if (any(lessThan(ci, ivec3(0))) || any(greaterThanEqual(ci, ivec3(gridDim))))
        return false;

    cell = CellIndex(uvec3(ci));
    return true;
}

bool TryLoadWorldMatrix(uint commandIndex, out mat4 M)
{
    if (!hasCulledCommands)
        return false;

    uint stride = max(culledCommandFloats, 48u);
    uint base = commandIndex * stride;
    if (base + 15u >= culledFloatCount)
        return false;

    vec4 c0 = vec4(culled[base+0], culled[base+4], culled[base+8],  culled[base+12]);
    vec4 c1 = vec4(culled[base+1], culled[base+5], culled[base+9],  culled[base+13]);
    vec4 c2 = vec4(culled[base+2], culled[base+6], culled[base+10], culled[base+14]);
    vec4 c3 = vec4(culled[base+3], culled[base+7], culled[base+11], culled[base+15]);
    M = mat4(c0, c1, c2, c3);
    return true;
}

void main()
{
    if (ScreenWidth <= 0.0 || ScreenHeight <= 0.0)
    {
        OutColor = vec4(0.0);
        return;
    }

    vec2 uv = gl_FragCoord.xy / vec2(ScreenWidth, ScreenHeight);
    
    // Get base scene color
    vec3 sceneColor = texture(HDRSceneTex, uv).rgb;
    sceneColor = sceneColor / (sceneColor + 1.0); // Simple tonemap
    
    float depth = texture(DepthView, uv).r;
    if (depth <= 0.0 || depth >= 1.0)
    {
        OutColor = vec4(sceneColor, 1.0);
        return;
    }

    vec3 worldPos = ReconstructWorldPosition(uv, depth);

    // Find the cell this pixel is in
    uint cell;
    if (!WorldToCell(worldPos, cell))
    {
        OutColor = vec4(sceneColor, 1.0);
        return;
    }

    uint count = counts[cell];
    if (count == 0u)
    {
        OutColor = vec4(sceneColor, 1.0);
        return;
    }

    // Check if we're inside any surfel's radius
    uint n = min(count, maxPerCell);
    float minDist = 1e10;
    vec3 nearestColor = vec3(0.0);
    bool foundSurfel = false;

    for (uint i = 0u; i < n; ++i)
    {
        uint idx = indices[cell * maxPerCell + i];
        if (idx >= maxSurfels)
            continue;
        if (surfels[idx].meta.y == 0u)
            continue;

        vec3 sPos = surfels[idx].posRadius.xyz;
        float sRadius = surfels[idx].posRadius.w;

        // Reconstruct world position from local space
        uint transformId = surfels[idx].meta.z;
        mat4 model;
        if (TryLoadWorldMatrix(transformId, model))
        {
            sPos = (model * vec4(sPos, 1.0)).xyz;
        }

        float dist = length(worldPos - sPos);
        
        // Check if within surfel radius - draw a circle
        if (dist < sRadius)
        {
            if (dist < minDist)
            {
                minDist = dist;
                // Color by surfel index for unique identification
                nearestColor = HashColor(idx);
                foundSurfel = true;
            }
        }
    }

    if (foundSurfel)
    {
        // Draw surfel as colored circle with edge highlight
        float edgeFactor = minDist / surfels[indices[cell * maxPerCell]].posRadius.w;
        float edgeHighlight = smoothstep(0.8, 1.0, edgeFactor);
        vec3 finalColor = mix(nearestColor, vec3(1.0), edgeHighlight * 0.3);
        
        // Blend with scene
        OutColor = vec4(mix(sceneColor, finalColor, 0.7), 1.0);
    }
    else
    {
        OutColor = vec4(sceneColor, 1.0);
    }
}
