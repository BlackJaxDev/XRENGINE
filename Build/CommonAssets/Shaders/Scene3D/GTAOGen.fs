#version 450

#include "AOCommon.glsl"
#pragma snippet "NormalEncoding"
#pragma snippet "DepthUtils"

const float PI = 3.14159265359f;
const float HALF_PI = 1.57079632679f;

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2D Normal;
uniform sampler2D DepthView;

uniform float Radius = 2.2f; // XRENGINE_FREQUENCY(View)
uniform float Bias = 0.06f; // XRENGINE_FREQUENCY(View)
uniform float Power = 1.35f; // XRENGINE_FREQUENCY(View)
uniform int SliceCount = 3; // XRENGINE_FREQUENCY(View)
uniform int StepsPerSlice = 4; // XRENGINE_FREQUENCY(View)
uniform float FalloffStartRatio = 0.4f; // XRENGINE_FREQUENCY(View)
uniform float ThicknessHeuristic = 1.0f; // XRENGINE_FREQUENCY(View)
uniform bool UseInputNormals = true; // XRENGINE_FREQUENCY(View)
uniform bool UseVisibilityBitmask = true; // XRENGINE_FREQUENCY(View)
uniform float VisibilityBitmaskThickness = 0.12f; // XRENGINE_FREQUENCY(View)
// Largest step radius in pixels of the depth input. Half-resolution inputs pass 96, so the
// largest radius on screen matches 192 full-resolution pixels.
uniform float MaxRadiusPixels = 192.0f; // XRENGINE_FREQUENCY(View)
// True when DepthView holds positive linear view depth (0 marks the far plane), written once at
// GTAO resolution by GTAOPrepareHalf.fs. False when it holds the raw depth buffer value.
uniform bool DepthViewIsLinear = false; // XRENGINE_FREQUENCY(View)

uniform mat4 ViewMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform mat4 ProjMatrix;

const uint VISIBILITY_BITMASK_SECTOR_COUNT = 32u;

// Reconstruction terms of the inverse projection. For a perspective projection (also off-axis
// or jittered), view position = vec3(clipXY * scale + bias, -1) * linearDepth. The terms come
// from the same matrix as the raw-depth path, so the jitter does not change.
struct GtaoReconstruction
{
    vec2 scale;
    vec2 bias;
    bool perspective;
};

GtaoReconstruction GtaoBuildReconstruction(mat4 inverseProjMatrix)
{
    GtaoReconstruction r;
    r.scale = vec2(inverseProjMatrix[0][0], inverseProjMatrix[1][1]);
    r.bias = vec2(inverseProjMatrix[3][0], inverseProjMatrix[3][1]);
    // The short form needs view z = -1 / w and x, y, w that do not mix the other clip terms.
    // The inverse comes from float math, so the zero terms and the -1 get a small tolerance; the
    // error that the short form then ignores stays within float precision.
    const float tolerance = 1e-6f;
    r.perspective =
        abs(inverseProjMatrix[1][0]) <= tolerance && abs(inverseProjMatrix[2][0]) <= tolerance &&
        abs(inverseProjMatrix[0][1]) <= tolerance && abs(inverseProjMatrix[2][1]) <= tolerance &&
        abs(inverseProjMatrix[0][2]) <= tolerance && abs(inverseProjMatrix[1][2]) <= tolerance &&
        abs(inverseProjMatrix[2][2]) <= tolerance && abs(inverseProjMatrix[3][2] + 1.0f) <= tolerance &&
        abs(inverseProjMatrix[0][3]) <= tolerance && abs(inverseProjMatrix[1][3]) <= tolerance;
    return r;
}

bool GtaoIsFarInput(float depthInput)
{
    return DepthViewIsLinear ? !(depthInput > 0.0f) : AOIsFarDepth(depthInput);
}

// View position of one DepthView sample.
vec3 GtaoViewPosition(float depthInput, vec2 uv, GtaoReconstruction r, mat4 inverseProjMatrix)
{
    if (!DepthViewIsLinear)
        return AOViewPosFromDepth(depthInput, uv, inverseProjMatrix);

    vec2 clipXY = AOClipXYFromTextureUV(uv);
    if (r.perspective)
        return vec3(clipXY * r.scale + r.bias, -1.0f) * depthInput;

    // Any other projection: solve the clip depth that gives this view depth, then use the matrix.
    vec4 fixedTerms = inverseProjMatrix[0] * clipXY.x + inverseProjMatrix[1] * clipXY.y + inverseProjMatrix[3];
    vec4 depthTerms = inverseProjMatrix[2];
    float clipZ = -(fixedTerms.z + depthInput * fixedTerms.w) / (depthTerms.z + depthInput * depthTerms.w);
    vec4 viewPosition = fixedTerms + depthTerms * clipZ;
    return viewPosition.xyz / max(viewPosition.w, 1e-5f);
}

// Fast polynomial acos approximation (max error ~0.02 rad)
float FastAcos(float x)
{
    float ax = abs(x);
    float r = (-0.0187293f * ax + 0.0742610f) * ax - 0.2121144f;
    r = (r * ax + 1.5707288f) * sqrt(max(1.0f - ax, 0.0f));
    return x < 0.0f ? PI - r : r;
}

vec3 GetViewNormal(vec2 uv, vec3 centerPos, GtaoReconstruction reconstruction)
{
    if (UseInputNormals)
    {
        vec3 worldNormal = XRENGINE_ReadNormal(Normal, uv);
        return normalize((ViewMatrix * vec4(worldNormal, 0.0f)).rgb);
    }

    vec2 texelSize = 1.0f / vec2(textureSize(DepthView, 0));
    vec2 uvX = clamp(uv + vec2(texelSize.x, 0.0f), vec2(0.0f), vec2(1.0f));
    vec2 uvY = clamp(uv + vec2(0.0f, texelSize.y), vec2(0.0f), vec2(1.0f));
    float depthX = texture(DepthView, uvX).r;
    float depthY = texture(DepthView, uvY).r;
    if (GtaoIsFarInput(depthX) || GtaoIsFarInput(depthY))
        return vec3(0.0f, 0.0f, 1.0f);
    vec3 posX = GtaoViewPosition(depthX, uvX, reconstruction, InverseProjMatrix);
    vec3 posY = GtaoViewPosition(depthY, uvY, reconstruction, InverseProjMatrix);
    vec3 deltaY = posY - centerPos;
    if (FramebufferTextureYDirection == 1)
        deltaY = -deltaY;
    return normalize(cross(posX - centerPos, deltaY));
}

float ComputeRadiusPixels(float radiusVS, float viewDepth)
{
    float projScale = abs(ProjMatrix[1][1]) * 0.5f * float(textureSize(DepthView, 0).y);
    return clamp(radiusVS * projScale / max(abs(viewDepth), 1e-3f), 1.0f, MaxRadiusPixels);
}

float InterleavedGradientNoise(vec2 pixel)
{
    return fract(52.9829189f * fract(dot(pixel, vec2(0.06711056f, 0.00583715f))));
}

float ComputeScreenEdgeFade(vec2 uv, float radiusPixels)
{
    vec2 screenSize = vec2(textureSize(DepthView, 0));
    vec2 edgePixels = min(uv, 1.0f - uv) * screenSize;
    float nearestEdgePixels = min(edgePixels.x, edgePixels.y);
    float fadePixels = clamp(radiusPixels * 0.08f, 2.0f, 8.0f);
    return smoothstep(0.0f, fadePixels, nearestEdgePixels);
}

// Canonical IntegrateArc from Jimenez et al. 2016 (Eq. 10)
float IntegrateArc(float h, float n)
{
    float hClamped = clamp(h, 0.0f, PI);
    float nClamped = clamp(n, -HALF_PI, HALF_PI);
    return 0.25f * (-cos(2.0f * hClamped - nClamped) + cos(nClamped) + 2.0f * hClamped * sin(nClamped));
}

uint UpdateSectors(float minHorizon, float maxHorizon, uint globalOccludedBitfield)
{
    float clampedMin = clamp(min(minHorizon, maxHorizon), 0.0f, 1.0f);
    float clampedMax = clamp(max(minHorizon, maxHorizon), 0.0f, 1.0f);
    if (clampedMax <= clampedMin)
        return globalOccludedBitfield;

    uint startSector = min(uint(clampedMin * float(VISIBILITY_BITMASK_SECTOR_COUNT)), VISIBILITY_BITMASK_SECTOR_COUNT - 1u);
    uint sectorSpan = uint(ceil((clampedMax - clampedMin) * float(VISIBILITY_BITMASK_SECTOR_COUNT)));
    if (sectorSpan == 0u)
        sectorSpan = 1u;
    sectorSpan = min(sectorSpan, VISIBILITY_BITMASK_SECTOR_COUNT - startSector);

    uint occludedRange = sectorSpan >= VISIBILITY_BITMASK_SECTOR_COUNT
        ? 0xFFFFFFFFu
        : (0xFFFFFFFFu >> int(VISIBILITY_BITMASK_SECTOR_COUNT - sectorSpan));

    return globalOccludedBitfield | (occludedRange << int(startSector));
}

uint AccumulateVisibilitySectors(vec3 deltaPos, vec3 viewDir, float normalAngle, float samplingDirection, float thickness, uint occludedSectors)
{
    float deltaLenSq = dot(deltaPos, deltaPos);
    if (deltaLenSq <= 1e-8f)
        return occludedSectors;

    // Scale backface thickness with distance so coarse raymarch steps form continuous
    // occlusion across walls and pillars instead of thin pinholes.
    float effectiveThickness = thickness + sqrt(deltaLenSq) * 0.45f;
    vec3 deltaPosBackface = deltaPos - viewDir * effectiveThickness;
    float deltaBackLenSq = dot(deltaPosBackface, deltaPosBackface);
    if (deltaBackLenSq <= 1e-8f)
        return occludedSectors;

    vec2 frontBackHorizon = vec2(dot(normalize(deltaPos), viewDir), dot(normalize(deltaPosBackface), viewDir));
    frontBackHorizon = vec2(
        FastAcos(clamp(frontBackHorizon.x, -1.0f, 1.0f)),
        FastAcos(clamp(frontBackHorizon.y, -1.0f, 1.0f)));

    frontBackHorizon = clamp(((samplingDirection * -frontBackHorizon) - normalAngle + HALF_PI) / PI, 0.0f, 1.0f);
    frontBackHorizon = samplingDirection >= 0.0f ? frontBackHorizon.yx : frontBackHorizon.xy;

    // Apply Bias as a tangent-plane guard band so flat surfaces never self-occlude
    // without shrinking real occluders at larger distances.
    float biasOffset = clamp(Bias / PI, 0.0f, 0.499f);
    float minHorizon = clamp(min(frontBackHorizon.x, frontBackHorizon.y), biasOffset, 1.0f - biasOffset);
    float maxHorizon = clamp(max(frontBackHorizon.x, frontBackHorizon.y), biasOffset, 1.0f - biasOffset);
    return UpdateSectors(minHorizon, maxHorizon, occludedSectors);
}

float ComputeSampleFalloff(vec3 delta, float dist, vec3 viewDir, float falloffStart, float radiusVS, float thicknessLimit)
{
    float falloff = 1.0f - smoothstep(falloffStart, radiusVS, dist);
    float depthBehind = max(0.0f, -dot(delta, viewDir));
    if (thicknessLimit > 0.0f)
        falloff *= 1.0f - smoothstep(0.0f, thicknessLimit, depthBehind);
    return clamp(falloff, 0.0f, 1.0f);
}

void main()
{
    if (FragPos.x > 1.0f || FragPos.y > 1.0f)
        discard;
    vec2 uv = AOTextureUVFromFragPos(FragPos);
    float invProjX = 1.0f / ProjMatrix[0][0];
    float invProjY = 1.0f / ProjMatrix[1][1];

    GtaoReconstruction reconstruction = GtaoBuildReconstruction(InverseProjMatrix);
    float depth = texture(DepthView, uv).r;
    if (GtaoIsFarInput(depth))
    {
        OutIntensity = 1.0f;
        return;
    }
    vec3 centerPos = GtaoViewPosition(depth, uv, reconstruction, InverseProjMatrix);
    vec3 centerNormal = GetViewNormal(uv, centerPos, reconstruction);
    vec3 viewDir = normalize(-centerPos);

    float radiusVS = max(Radius, 0.001f);
    float radiusPixels = ComputeRadiusPixels(radiusVS, centerPos.z);
    float falloffStart = clamp(FalloffStartRatio, 0.0f, 1.0f) * radiusVS;
    vec2 texelSize = 1.0f / vec2(textureSize(DepthView, 0));
    float thicknessLimit = radiusVS * clamp(ThicknessHeuristic, 0.0f, 1.0f);
    float bitmaskThickness = max(VisibilityBitmaskThickness, 1e-4f);

    int sliceCount = clamp(SliceCount, 1, 8);
    int stepCount = clamp(StepsPerSlice, 1, 16);
    vec2 noiseCoord = XRENGINE_ScreenNoiseCoord(gl_FragCoord.xy, vec2(0.0), vec2(textureSize(DepthView, 0)));
    // Use the Jimenez et al. 2016 4x4 spatial direction and step-jitter pattern so
    // the bilateral blur removes rotation noise without low-frequency diagonal stripes.
    ivec2 noisePixel = ivec2(noiseCoord);
    float spatialDirection = float((((noisePixel.x + noisePixel.y) & 3) << 2) + (noisePixel.x & 3)) / 16.0f;
    float baseAngle = (spatialDirection - 0.5f) * (PI / float(sliceCount));
    float stepJitter = float((noisePixel.y - noisePixel.x) & 3) * 0.25f + 0.125f;

    float visibility = 0.0f;
    float weightSum = 0.0f;

    for (int sliceIndex = 0; sliceIndex < sliceCount; ++sliceIndex)
    {
        float phi = baseAngle + (PI * float(sliceIndex)) / float(sliceCount);
        vec2 screenDir = vec2(cos(phi), sin(phi));
        vec2 clipDir = AOClipDirectionFromTextureDirection(screenDir);

        // Derive the slice tangent analytically from the pixel raymarch direction
        // (scaled by texelSize * invProj so non-square viewports stay aligned).
        vec3 sliceTangent = normalize(vec3(clipDir.x * texelSize.x * invProjX, clipDir.y * texelSize.y * invProjY, 0.0f));
        sliceTangent = normalize(sliceTangent - viewDir * dot(sliceTangent, viewDir));
        vec3 slicePlaneN = normalize(cross(viewDir, sliceTangent));

        // Project surface normal onto the slice plane
        vec3 projN = centerNormal - slicePlaneN * dot(centerNormal, slicePlaneN);
        float projNLen = length(projN);
        if (projNLen <= 1e-5f)
        {
            visibility += 1.0f;
            weightSum += 1.0f;
            continue;
        }
        projN /= projNLen;

        // Signed normal angle from view direction within the slice plane
        float gamma = atan(dot(projN, sliceTangent), dot(projN, viewDir));
        // Visibility-bitmask sector mapping uses the original GTAO N handedness.
        float visibilityBitmaskNormalAngle = -gamma;

        // Unoccluded horizon angles sit on the surface tangent plane.
        float h1Tangent = clamp(HALF_PI + gamma, 0.0f, PI);
        float h2Tangent = clamp(HALF_PI - gamma, 0.0f, PI);
        float h1 = h1Tangent;
        float h2 = h2Tangent;
        uint occludedSectors = 0u;
        float occludedSectorWeight = 0.0f;

        for (int step = 0; step < stepCount; ++step)
        {
            float t = (float(step) + stepJitter) / float(stepCount);
            vec2 offset = screenDir * texelSize * radiusPixels * t;

            // --- Forward sample ---
            vec2 fUV = uv + offset;
            if (fUV.x > 0.0f && fUV.x < 1.0f && fUV.y > 0.0f && fUV.y < 1.0f)
            {
                float fDepth = texture(DepthView, fUV).r;
                if (!GtaoIsFarInput(fDepth))
                {
                    vec3 fPos = GtaoViewPosition(fDepth, fUV, reconstruction, InverseProjMatrix);
                    vec3 delta = fPos - centerPos;
                    float dist = length(delta);

                    if (dist > 1e-4f && dist <= radiusVS)
                    {
                        if (UseVisibilityBitmask)
                        {
                            float falloff = ComputeSampleFalloff(delta, dist, viewDir, falloffStart, radiusVS, thicknessLimit);
                            if (falloff > 0.01f)
                            {
                                uint previousSectors = occludedSectors;
                                uint updatedSectors = AccumulateVisibilitySectors(delta, viewDir, visibilityBitmaskNormalAngle, 1.0f, bitmaskThickness * falloff, occludedSectors);
                                occludedSectorWeight += float(bitCount(updatedSectors & ~previousSectors)) * falloff;
                                occludedSectors = updatedSectors;
                            }
                        }
                        else
                        {
                            vec3 deltaDir = delta / dist;

                            // Project onto slice plane and compute horizon angle from view direction
                            vec3 projDelta = deltaDir - slicePlaneN * dot(deltaDir, slicePlaneN);
                            float projLen = length(projDelta);
                            if (projLen > 1e-5f)
                            {
                                projDelta /= projLen;
                                float candidateH = FastAcos(clamp(dot(projDelta, viewDir), -1.0f, 1.0f));

                                float falloff = ComputeSampleFalloff(delta, dist, viewDir, falloffStart, radiusVS, thicknessLimit);
                                candidateH = mix(h1Tangent, candidateH, falloff);
                                candidateH = clamp(candidateH + Bias, 0.0f, h1Tangent);
                                h1 = min(h1, candidateH);
                            }
                        }
                    }
                }
            }

            // --- Backward sample ---
            vec2 bUV = uv - offset;
            if (bUV.x > 0.0f && bUV.x < 1.0f && bUV.y > 0.0f && bUV.y < 1.0f)
            {
                float bDepth = texture(DepthView, bUV).r;
                if (!GtaoIsFarInput(bDepth))
                {
                    vec3 bPos = GtaoViewPosition(bDepth, bUV, reconstruction, InverseProjMatrix);
                    vec3 delta = bPos - centerPos;
                    float dist = length(delta);

                    if (dist > 1e-4f && dist <= radiusVS)
                    {
                        if (UseVisibilityBitmask)
                        {
                            float falloff = ComputeSampleFalloff(delta, dist, viewDir, falloffStart, radiusVS, thicknessLimit);
                            if (falloff > 0.01f)
                            {
                                uint previousSectors = occludedSectors;
                                uint updatedSectors = AccumulateVisibilitySectors(delta, viewDir, visibilityBitmaskNormalAngle, -1.0f, bitmaskThickness * falloff, occludedSectors);
                                occludedSectorWeight += float(bitCount(updatedSectors & ~previousSectors)) * falloff;
                                occludedSectors = updatedSectors;
                            }
                        }
                        else
                        {
                            vec3 deltaDir = delta / dist;

                            vec3 projDelta = deltaDir - slicePlaneN * dot(deltaDir, slicePlaneN);
                            float projLen = length(projDelta);
                            if (projLen > 1e-5f)
                            {
                                projDelta /= projLen;
                                float candidateH = FastAcos(clamp(dot(projDelta, viewDir), -1.0f, 1.0f));

                                float falloff = ComputeSampleFalloff(delta, dist, viewDir, falloffStart, radiusVS, thicknessLimit);
                                candidateH = mix(h2Tangent, candidateH, falloff);
                                candidateH = clamp(candidateH + Bias, 0.0f, h2Tangent);
                                h2 = min(h2, candidateH);
                            }
                        }
                    }
                }
            }
        }

        float sliceWeight = UseVisibilityBitmask
            ? 1.0f
            : projNLen * max(cos(gamma) + gamma * sin(gamma), 1e-4f);
        float sliceVis = UseVisibilityBitmask
            ? clamp(1.0f - occludedSectorWeight / float(VISIBILITY_BITMASK_SECTOR_COUNT), 0.0f, 1.0f)
            : max(projNLen * (IntegrateArc(h1, gamma) + IntegrateArc(h2, -gamma)), 0.0f);
        visibility += sliceVis;
        weightSum += sliceWeight;
    }

    visibility = clamp(visibility / max(weightSum, 1e-5f), 0.0f, 1.0f);
    visibility = mix(1.0f, visibility, ComputeScreenEdgeFade(uv, radiusPixels));
    OutIntensity = visibility;
}
