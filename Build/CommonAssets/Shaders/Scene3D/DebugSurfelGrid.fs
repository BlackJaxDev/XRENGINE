#version 450 core

// Surfel Debug Visualization: Renders surfel grid cell occupancy
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

// Grid information
uniform vec3 gridOrigin;
uniform float cellSize;
uniform uvec3 gridDim;
uniform uint maxPerCell;

layout(std430, binding = 3) buffer GridCounts
{
    uint counts[];
};

vec3 HeatmapColor(float t)
{
    // Blue -> Cyan -> Green -> Yellow -> Red
    t = clamp(t, 0.0, 1.0);
    vec3 c;
    if (t < 0.25)
    {
        float s = t / 0.25;
        c = mix(vec3(0.0, 0.0, 1.0), vec3(0.0, 1.0, 1.0), s);
    }
    else if (t < 0.5)
    {
        float s = (t - 0.25) / 0.25;
        c = mix(vec3(0.0, 1.0, 1.0), vec3(0.0, 1.0, 0.0), s);
    }
    else if (t < 0.75)
    {
        float s = (t - 0.5) / 0.25;
        c = mix(vec3(0.0, 1.0, 0.0), vec3(1.0, 1.0, 0.0), s);
    }
    else
    {
        float s = (t - 0.75) / 0.25;
        c = mix(vec3(1.0, 1.0, 0.0), vec3(1.0, 0.0, 0.0), s);
    }
    return c;
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

bool WorldToCell(vec3 p, out uint cell, out vec3 cellLocalPos)
{
    vec3 rel = (p - gridOrigin) / max(cellSize, 1e-6);
    ivec3 ci = ivec3(floor(rel));
    if (any(lessThan(ci, ivec3(0))) || any(greaterThanEqual(ci, ivec3(gridDim))))
        return false;

    cell = CellIndex(uvec3(ci));
    cellLocalPos = fract(rel); // Position within cell [0,1]
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
    vec3 cellLocalPos;
    if (!WorldToCell(worldPos, cell, cellLocalPos))
    {
        OutColor = vec4(sceneColor, 1.0);
        return;
    }

    uint count = counts[cell];
    
    // Color by occupancy - normalize to maxPerCell
    float occupancy = float(count) / float(maxPerCell);
    vec3 heatColor = HeatmapColor(occupancy);
    
    // Draw cell boundaries as darker lines
    float edgeWidth = 0.05;
    bool onEdge = any(lessThan(cellLocalPos, vec3(edgeWidth))) || 
                  any(greaterThan(cellLocalPos, vec3(1.0 - edgeWidth)));
    
    if (onEdge)
    {
        heatColor *= 0.5; // Darken edges
    }
    
    // Show empty cells as gray
    if (count == 0u)
    {
        heatColor = vec3(0.2, 0.2, 0.2);
    }
    
    // Blend with scene
    OutColor = vec4(mix(sceneColor, heatColor, 0.6), 1.0);
}
