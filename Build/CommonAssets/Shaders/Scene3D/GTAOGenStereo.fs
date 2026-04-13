#version 460
#extension GL_OVR_multiview2 : require

#pragma snippet "NormalEncoding"
#pragma snippet "DepthUtils"

const float PI = 3.14159265359f;
const float HALF_PI = 1.57079632679f;

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray Normal;
uniform sampler2DArray DepthView;

uniform float Radius = 2.0f;
uniform float Bias = 0.05f;
uniform float Power = 1.0f; // Unused in gather — applied post-denoise in DeferredLightCombine
uniform int SliceCount = 3;
uniform int StepsPerSlice = 6;
uniform float FalloffStartRatio = 0.4f;
uniform float ThicknessHeuristic = 1.0f;
uniform bool UseInputNormals = true;
uniform bool UseVisibilityBitmask = false;
uniform float VisibilityBitmaskThickness = 0.15f;

uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;
uniform mat4 LeftEyeViewMatrix;
uniform mat4 RightEyeViewMatrix;
uniform mat4 LeftEyeInverseProjMatrix;
uniform mat4 RightEyeInverseProjMatrix;
uniform mat4 LeftEyeProjMatrix;
uniform mat4 RightEyeProjMatrix;

const uint VISIBILITY_BITMASK_SECTOR_COUNT = 32u;

bool AOIsFarDepth(float depth)
{
    const float eps = 1e-6f;
    return DepthMode == 1 ? depth <= eps : depth >= 1.0f - eps;
}

// Fast polynomial acos approximation (max error ~0.02 rad)
float FastAcos(float x)
{
    float ax = abs(x);
    float r = (-0.0187293f * ax + 0.0742610f) * ax - 0.2121144f;
    r = (r * ax + 1.5707288f) * sqrt(max(1.0f - ax, 0.0f));
    return x < 0.0f ? PI - r : r;
}

vec3 GetViewNormal(vec2 uv, vec3 centerPos, mat4 viewMatrix, float invProjX, float invProjY, float projWScale, float projWBias)
{
    if (UseInputNormals)
    {
        vec3 worldNormal = XRENGINE_ReadNormal(Normal, vec3(uv, gl_ViewID_OVR));
        return normalize((viewMatrix * vec4(worldNormal, 0.0f)).rgb);
    }

    vec2 texelSize = 1.0f / textureSize(DepthView, 0).xy;
    vec2 uvX = clamp(uv + vec2(texelSize.x, 0.0f), vec2(0.0f), vec2(1.0f));
    vec2 uvY = clamp(uv + vec2(0.0f, texelSize.y), vec2(0.0f), vec2(1.0f));
    float depthX = texture(DepthView, vec3(uvX, gl_ViewID_OVR)).r;
    float depthY = texture(DepthView, vec3(uvY, gl_ViewID_OVR)).r;
    if (AOIsFarDepth(depthX) || AOIsFarDepth(depthY))
        return vec3(0.0f, 0.0f, 1.0f);
    vec3 posX = XRENGINE_ViewPosFromDepthFast(depthX, uvX, invProjX, invProjY, projWScale, projWBias);
    vec3 posY = XRENGINE_ViewPosFromDepthFast(depthY, uvY, invProjX, invProjY, projWScale, projWBias);
    return normalize(cross(posX - centerPos, posY - centerPos));
}

float ComputeRadiusPixels(float radiusVS, float viewDepth, mat4 projMatrix)
{
    float projScale = abs(projMatrix[1][1]) * 0.5f * float(textureSize(DepthView, 0).y);
    return clamp(radiusVS * projScale / max(abs(viewDepth), 1e-3f), 1.0f, 192.0f);
}

float InterleavedGradientNoise(vec2 pixel)
{
    return fract(52.9829189f * fract(dot(pixel, vec2(0.06711056f, 0.00583715f))));
}

// Canonical IntegrateArc from Jimenez et al. 2016 (Eq. 10)
float IntegrateArc(float h, float n)
{
    float hClamped = clamp(h, 0.0f, HALF_PI);
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

    vec3 deltaPosBackface = deltaPos - viewDir * thickness;
    float deltaBackLenSq = dot(deltaPosBackface, deltaPosBackface);
    if (deltaBackLenSq <= 1e-8f)
        return occludedSectors;

    vec2 frontBackHorizon = vec2(dot(normalize(deltaPos), viewDir), dot(normalize(deltaPosBackface), viewDir));
    frontBackHorizon = vec2(
        FastAcos(clamp(frontBackHorizon.x, -1.0f, 1.0f)),
        FastAcos(clamp(frontBackHorizon.y, -1.0f, 1.0f)));

    frontBackHorizon = clamp(((samplingDirection * -frontBackHorizon) - normalAngle + HALF_PI) / PI, 0.0f, 1.0f);
    frontBackHorizon = samplingDirection >= 0.0f ? frontBackHorizon.yx : frontBackHorizon.xy;

    float biasOffset = clamp(Bias / PI, 0.0f, 0.499f);
    float minHorizon = min(frontBackHorizon.x, frontBackHorizon.y) + biasOffset;
    float maxHorizon = max(frontBackHorizon.x, frontBackHorizon.y) - biasOffset;
    return UpdateSectors(minHorizon, maxHorizon, occludedSectors);
}

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;
    uv = uv * 0.5f + 0.5f;

    bool leftEye = gl_ViewID_OVR == 0;
    mat4 viewMatrix = leftEye ? LeftEyeViewMatrix : RightEyeViewMatrix;
    mat4 inverseProjMatrix = leftEye ? LeftEyeInverseProjMatrix : RightEyeInverseProjMatrix;
    mat4 projMatrix = leftEye ? LeftEyeProjMatrix : RightEyeProjMatrix;

    // Precompute fast reconstruction constants once per pixel
    float invProjX = 1.0f / projMatrix[0][0];
    float invProjY = 1.0f / projMatrix[1][1];
    float projWScale = inverseProjMatrix[2][3];
    float projWBias  = inverseProjMatrix[3][3];

    float depth = texture(DepthView, vec3(uv, gl_ViewID_OVR)).r;
    if (AOIsFarDepth(depth))
    {
        OutIntensity = 1.0f;
        return;
    }
    vec3 centerPos = XRENGINE_ViewPosFromDepthFast(depth, uv, invProjX, invProjY, projWScale, projWBias);
    vec3 centerNormal = GetViewNormal(uv, centerPos, viewMatrix, invProjX, invProjY, projWScale, projWBias);
    vec3 viewDir = normalize(-centerPos);

    float radiusVS = max(Radius, 0.001f);
    float radiusPixels = ComputeRadiusPixels(radiusVS, centerPos.z, projMatrix);
    float falloffStart = clamp(FalloffStartRatio, 0.0f, 1.0f) * radiusVS;
    vec2 texelSize = 1.0f / textureSize(DepthView, 0).xy;
    float thicknessLimit = radiusVS * clamp(ThicknessHeuristic, 0.0f, 1.0f);
    float bitmaskThickness = max(VisibilityBitmaskThickness, 1e-4f);

    int sliceCount = clamp(SliceCount, 1, 8);
    int stepCount = clamp(StepsPerSlice, 1, 16);
    float baseAngle = (InterleavedGradientNoise(floor(gl_FragCoord.xy) + vec2(float(gl_ViewID_OVR) * 13.0f, 0.0f)) - 0.5f) * (PI / float(sliceCount));
    float stepJitter = InterleavedGradientNoise(floor(gl_FragCoord.xy) + vec2(17.0f, 43.0f + float(gl_ViewID_OVR) * 19.0f));

    float visibility = 0.0f;

    for (int sliceIndex = 0; sliceIndex < sliceCount; ++sliceIndex)
    {
        float phi = baseAngle + (PI * float(sliceIndex)) / float(sliceCount);
        vec2 screenDir = vec2(cos(phi), sin(phi));

        vec3 sliceTangent = normalize(vec3(screenDir.x * invProjX, screenDir.y * invProjY, 0.0f));
        sliceTangent = normalize(sliceTangent - viewDir * dot(sliceTangent, viewDir));
        vec3 slicePlaneN = normalize(cross(viewDir, sliceTangent));

        vec3 projN = centerNormal - slicePlaneN * dot(centerNormal, slicePlaneN);
        float projNLen = length(projN);
        if (projNLen <= 1e-5f)
        {
            visibility += 1.0f;
            continue;
        }
        projN /= projNLen;

        float gamma = atan(dot(projN, sliceTangent), dot(projN, viewDir));

        float h1 = HALF_PI;
        float h2 = HALF_PI;
        uint occludedSectors = 0u;

        for (int step = 0; step < stepCount; ++step)
        {
            float t = (float(step) + stepJitter) / float(stepCount);
            vec2 offset = screenDir * texelSize * radiusPixels * t;

            // --- Forward sample ---
            vec2 fUV = uv + offset;
            if (fUV.x > 0.0f && fUV.x < 1.0f && fUV.y > 0.0f && fUV.y < 1.0f)
            {
                float fDepth = texture(DepthView, vec3(fUV, gl_ViewID_OVR)).r;
                if (!AOIsFarDepth(fDepth))
                {
                    vec3 fPos = XRENGINE_ViewPosFromDepthFast(fDepth, fUV, invProjX, invProjY, projWScale, projWBias);
                    vec3 delta = fPos - centerPos;
                    float dist = length(delta);

                    if (dist > 1e-4f && dist <= radiusVS)
                    {
                        if (UseVisibilityBitmask)
                        {
                            occludedSectors = AccumulateVisibilitySectors(delta, viewDir, gamma, 1.0f, bitmaskThickness, occludedSectors);
                        }
                        else
                        {
                            vec3 deltaDir = delta / dist;
                            vec3 projDelta = deltaDir - slicePlaneN * dot(deltaDir, slicePlaneN);
                            float projLen = length(projDelta);
                            if (projLen > 1e-5f)
                            {
                                projDelta /= projLen;
                                float candidateH = FastAcos(clamp(dot(projDelta, viewDir), 0.0f, 1.0f));

                                float falloff = 1.0f - smoothstep(falloffStart, radiusVS, dist);

                                float depthBehind = max(0.0f, -dot(delta, viewDir));
                                if (thicknessLimit > 0.0f)
                                    falloff *= 1.0f - smoothstep(0.0f, thicknessLimit, depthBehind);

                                candidateH = mix(HALF_PI, candidateH, falloff);
                                candidateH = clamp(candidateH + Bias, 0.0f, HALF_PI);
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
                float bDepth = texture(DepthView, vec3(bUV, gl_ViewID_OVR)).r;
                if (!AOIsFarDepth(bDepth))
                {
                    vec3 bPos = XRENGINE_ViewPosFromDepthFast(bDepth, bUV, invProjX, invProjY, projWScale, projWBias);
                    vec3 delta = bPos - centerPos;
                    float dist = length(delta);

                    if (dist > 1e-4f && dist <= radiusVS)
                    {
                        if (UseVisibilityBitmask)
                        {
                            occludedSectors = AccumulateVisibilitySectors(delta, viewDir, gamma, -1.0f, bitmaskThickness, occludedSectors);
                        }
                        else
                        {
                            vec3 deltaDir = delta / dist;
                            vec3 projDelta = deltaDir - slicePlaneN * dot(deltaDir, slicePlaneN);
                            float projLen = length(projDelta);
                            if (projLen > 1e-5f)
                            {
                                projDelta /= projLen;
                                float candidateH = FastAcos(clamp(dot(projDelta, viewDir), 0.0f, 1.0f));

                                float falloff = 1.0f - smoothstep(falloffStart, radiusVS, dist);

                                float depthBehind = max(0.0f, -dot(delta, viewDir));
                                if (thicknessLimit > 0.0f)
                                    falloff *= 1.0f - smoothstep(0.0f, thicknessLimit, depthBehind);

                                candidateH = mix(HALF_PI, candidateH, falloff);
                                candidateH = clamp(candidateH + Bias, 0.0f, HALF_PI);
                                h2 = min(h2, candidateH);
                            }
                        }
                    }
                }
            }
        }

        float sliceVis = UseVisibilityBitmask
            ? 1.0f - float(bitCount(occludedSectors)) / float(VISIBILITY_BITMASK_SECTOR_COUNT)
            : projNLen * (IntegrateArc(h1, gamma) + IntegrateArc(h2, -gamma));
        visibility += clamp(sliceVis, 0.0f, 1.0f);
    }

    visibility = clamp(visibility / float(sliceCount), 0.0f, 1.0f);
    OutIntensity = visibility;
}