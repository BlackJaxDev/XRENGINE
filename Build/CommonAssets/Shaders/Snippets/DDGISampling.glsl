// DDGISampling.glsl
// Usage: #pragma snippet "DDGISampling"
#ifndef XR_DDGI_SAMPLING_INCLUDED
#define XR_DDGI_SAMPLING_INCLUDED

#pragma snippet "DDGIBindings"
#pragma snippet "OctahedralMapping"

#ifndef XR_DDGI_PROBE_STRUCT_DEFINED
#define XR_DDGI_PROBE_STRUCT_DEFINED
struct DDGIProbeGPU
{
    vec4 position;          // xyz = base world pos, w = active/sleeping state
    vec4 relocationOffset; // xyz = relocation offset, w = confidence
};
#endif

#ifndef XR_DDGI_PROBE_BUFFER_DECLARED
#define XR_DDGI_PROBE_BUFFER_DECLARED
layout(std430, XR_DDGI_STORAGE_BINDING(4)) readonly buffer ProbeStateBuffer
{
    DDGIProbeGPU gProbes[];
};
#endif

// Screen sampling uses units 3/4. Passes that bind the same atlas names at
// different units must override these before including this snippet.
#ifndef DDGI_IRRADIANCE_BINDING
#define DDGI_IRRADIANCE_BINDING 3
#endif
#ifndef DDGI_VISIBILITY_BINDING
#define DDGI_VISIBILITY_BINDING 4
#endif
layout(XR_DDGI_SAMPLER_BINDING(DDGI_IRRADIANCE_BINDING)) uniform sampler2DArray uDDGIIrradianceAtlas;
layout(XR_DDGI_SAMPLER_BINDING(DDGI_VISIBILITY_BINDING)) uniform sampler2DArray uDDGIVisibilityAtlas;

// Single volume legacy fallback uniforms
uniform vec3 uGridMin;
uniform vec3 uProbeSpacing;
uniform ivec3 uProbeCounts;
uniform float uNormalBias;
uniform float uViewBias;
uniform float uChebyshevPower;
uniform float uIntensity;
uniform vec3 uTint;
uniform vec2 uIrradianceAtlasSize;
uniform vec2 uVisibilityAtlasSize;

// Cascade hierarchy uniforms
uniform int uCascadeCount;
uniform vec3 uCascadeGridMin[4];
uniform vec3 uCascadeProbeSpacing[4];
uniform ivec3 uCascadeProbeCounts[4];
uniform vec3 uCascadeHalfExtents[4];
uniform int uCascadeProbeOffset[4];
uniform float uCascadeBlendMargin;
uniform int uDisableVisibilityOnCoarse;

vec3 sampleDDGICascade(vec3 worldPos, vec3 normal, vec3 viewDir, int cascadeIndex)
{
    float normalBias = max(uNormalBias, 0.0);
    float viewBias = max(uViewBias, 0.0);
    float chebyshevPower = max(uChebyshevPower, 0.0);
    float intensity = max(uIntensity, 0.0);

    vec3 cascadeGridMin = (uCascadeCount > 0) ? uCascadeGridMin[cascadeIndex] : uGridMin;
    vec3 cascadeSpacing = (uCascadeCount > 0) ? uCascadeProbeSpacing[cascadeIndex] : uProbeSpacing;
    ivec3 cascadeCounts = (uCascadeCount > 0) ? uCascadeProbeCounts[cascadeIndex] : uProbeCounts;
    int cascadeProbeOffset = (uCascadeCount > 0) ? uCascadeProbeOffset[cascadeIndex] : 0;

    vec3 biasedPos = worldPos + normal * normalBias + viewDir * viewBias;
    vec3 spacing = max(cascadeSpacing, vec3(0.001));
    vec3 gridCoord = (biasedPos - cascadeGridMin) / spacing;
    ivec3 baseProbe = ivec3(floor(gridCoord));

    // Clamp to valid probe grid cells
    ivec3 maxCell = max(ivec3(0), cascadeCounts - ivec3(2));
    baseProbe = clamp(baseProbe, ivec3(0), maxCell);
    // Compute weights relative to the clamped cell. fract(gridCoord) would
    // select the penultimate probe at the exact upper grid boundary and wrap
    // biased points below the minimum toward the second probe.
    vec3 alpha = clamp(gridCoord - vec3(baseProbe), 0.0, 1.0);

    int xCorners = cascadeCounts.x > 1 ? 2 : 1;
    int yCorners = cascadeCounts.y > 1 ? 2 : 1;
    int zCorners = cascadeCounts.z > 1 ? 2 : 1;
    int cornerCount = xCorners * yCorners * zCorners;

    vec3 accumulatedIrradiance = vec3(0.0);
    float totalWeight = 0.0;

    bool skipVisibility = (uDisableVisibilityOnCoarse != 0) && (uCascadeCount > 1) && (cascadeIndex == uCascadeCount - 1);

    for (int i = 0; i < cornerCount; ++i)
    {
        ivec3 offset = ivec3(
            i % xCorners,
            (i / xCorners) % yCorners,
            i / (xCorners * yCorners));
        ivec3 probeGrid = baseProbe + offset;
        int localProbeIndex = probeGrid.x + probeGrid.y * cascadeCounts.x + probeGrid.z * (cascadeCounts.x * cascadeCounts.y);
        int globalProbeIndex = cascadeProbeOffset + localProbeIndex;

        // Check probe classification state: 1.0 = Inactive (embedded inside geometry)
        float probeState = gProbes[globalProbeIndex].position.w;
        if (probeState > 0.5 && probeState < 1.5)
        {
            // Embedded probe contributes 0 weight to avoid light leaks from inside walls
            continue;
        }

        // Probe base world position + relocation offset
        vec3 probeWorldPos = cascadeGridMin + vec3(probeGrid) * spacing;
        probeWorldPos += gProbes[globalProbeIndex].relocationOffset.xyz;

        // Vector from surface to probe
        vec3 pointToProbe = probeWorldPos - biasedPos;
        float dist = length(pointToProbe);
        vec3 dirToProbe = dist > 1e-4 ? (pointToProbe / dist) : vec3(0.0, 1.0, 0.0);

        // Cosine weight between surface normal and direction to probe
        float wCosine = max(0.0001, dot(normal, dirToProbe));

        // Trilinear interpolation weight
        vec3 trilinear = vec3(
            xCorners == 1 ? 1.0 : mix(1.0 - alpha.x, alpha.x, float(offset.x)),
            yCorners == 1 ? 1.0 : mix(1.0 - alpha.y, alpha.y, float(offset.y)),
            zCorners == 1 ? 1.0 : mix(1.0 - alpha.z, alpha.z, float(offset.z)));
        float wGrid = trilinear.x * trilinear.y * trilinear.z;

        float wVis = 1.0;
        if (!skipVisibility)
        {
            // Chebyshev visibility test
            // Direction from probe to surface is -dirToProbe
            vec3 probeToPointDir = -dirToProbe;
            vec2 octVisUV = XRENGINE_EncodeOcta(probeToPointDir);

            // Compute atlas UV for probe visibility tile (16x16 with 14x14 interior)
            int tx = localProbeIndex % max(1, cascadeCounts.x);
            int ty = localProbeIndex / max(1, cascadeCounts.x);
            vec2 visTileOrigin = vec2(float(tx), float(ty)) * 16.0;
            vec2 visTexelCoord = visTileOrigin + vec2(1.0) + octVisUV * 14.0;
            vec2 visUV = visTexelCoord / max(uVisibilityAtlasSize, vec2(1.0));

            vec2 moments = textureLod(uDDGIVisibilityAtlas, vec3(visUV, float(cascadeIndex)), 0.0).rg;
            float meanDist = moments.x;
            float meanSqDist = moments.y;

            if (dist > meanDist)
            {
                float variance = max(meanSqDist - meanDist * meanDist, 0.0001);
                float diff = dist - meanDist;
                float pMax = variance / (variance + diff * diff);
                float threshold = 0.2;
                wVis = clamp((pMax - threshold) / (1.0 - threshold), 0.0, 1.0);
                wVis = pow(wVis, chebyshevPower);
            }
        }

        float weight = wGrid * wCosine * wVis;

        // Sample irradiance for surface normal
        vec2 octIrrUV = XRENGINE_EncodeOcta(normal);
        int tx = localProbeIndex % max(1, cascadeCounts.x);
        int ty = localProbeIndex / max(1, cascadeCounts.x);
        vec2 irrTileOrigin = vec2(float(tx), float(ty)) * 6.0;
        vec2 irrTexelCoord = irrTileOrigin + vec2(1.0) + octIrrUV * 4.0;
        vec2 irrUV = irrTexelCoord / max(uIrradianceAtlasSize, vec2(1.0));

        vec3 probeIrradiance = textureLod(uDDGIIrradianceAtlas, vec3(irrUV, float(cascadeIndex)), 0.0).rgb;

        accumulatedIrradiance += weight * probeIrradiance;
        totalWeight += weight;
    }

    vec3 result = totalWeight > 1e-4 ? (accumulatedIrradiance / totalWeight) : vec3(0.0);
    return result * intensity;
}

float DDGI_CascadeNormalizedDistance(vec3 worldPos, vec3 gridMin, vec3 halfExtents, ivec3 counts)
{
    vec3 center = gridMin + halfExtents * vec3(greaterThan(counts, ivec3(1)));
    vec3 normDist = abs(worldPos - center) / max(halfExtents, vec3(1e-4));
    return max(normDist.x, max(normDist.y, normDist.z));
}

float DDGI_OuterFade(float normalizedDistance)
{
    return 1.0 - smoothstep(1.0 - clamp(uCascadeBlendMargin, 0.01, 0.5), 1.0, normalizedDistance);
}

// Keep feedback untinted: tint is a presentation control for the final sampled
// lighting and must not accumulate into every DDGI bounce.
vec3 sampleDDGIUntinted(vec3 worldPos, vec3 normal, vec3 viewDir)
{
    if (uCascadeCount <= 1)
    {
        vec3 halfExtents = uCascadeCount > 0
            ? uCascadeHalfExtents[0]
            : max((vec3(max(uProbeCounts - ivec3(1), ivec3(0))) * uProbeSpacing) * 0.5, vec3(1e-4));
        float normalizedDistance = DDGI_CascadeNormalizedDistance(worldPos, uGridMin, halfExtents, uProbeCounts);
        return sampleDDGICascade(worldPos, normal, viewDir, 0) * DDGI_OuterFade(normalizedDistance);
    }

    float blendMargin = clamp(uCascadeBlendMargin, 0.01, 0.5);
    float blendStart = 1.0 - blendMargin;

    // Traverse from finest cascade (0) to coarsest (uCascadeCount - 1)
    for (int k = 0; k < uCascadeCount; ++k)
    {
        float maxDist = DDGI_CascadeNormalizedDistance(worldPos, uCascadeGridMin[k], uCascadeHalfExtents[k], uCascadeProbeCounts[k]);

        if (maxDist <= 1.0)
        {
            // Inside cascade k. Check if within outer blend margin to cascade k + 1
            if (maxDist > blendStart && k < uCascadeCount - 1)
            {
                float alpha = smoothstep(blendStart, 1.0, maxDist);
                vec3 gi0 = sampleDDGICascade(worldPos, normal, viewDir, k);
                vec3 gi1 = sampleDDGICascade(worldPos, normal, viewDir, k + 1);
                return mix(gi0, gi1, alpha);
            }
            return sampleDDGICascade(worldPos, normal, viewDir, k) * (k == uCascadeCount - 1 ? DDGI_OuterFade(maxDist) : 1.0);
        }
    }

    // Beyond all cascades: sample coarsest cascade with smooth fade-out
    int coarseIndex = uCascadeCount - 1;
    float coarseMaxDist = DDGI_CascadeNormalizedDistance(worldPos, uCascadeGridMin[coarseIndex], uCascadeHalfExtents[coarseIndex], uCascadeProbeCounts[coarseIndex]);
    float outerFade = DDGI_OuterFade(coarseMaxDist);

    return sampleDDGICascade(worldPos, normal, viewDir, coarseIndex) * outerFade;
}

vec3 sampleDDGI(vec3 worldPos, vec3 normal, vec3 viewDir)
{
    return sampleDDGIUntinted(worldPos, normal, viewDir) * uTint;
}

vec3 sampleDDGI(vec3 worldPos, vec3 normal)
{
    return sampleDDGI(worldPos, normal, vec3(0.0));
}

#endif // XR_DDGI_SAMPLING_INCLUDED
