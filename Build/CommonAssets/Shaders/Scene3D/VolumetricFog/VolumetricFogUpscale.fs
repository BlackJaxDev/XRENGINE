#version 450

// Volumetric fog bilateral upscale.
//
// Reads:
//   VolumetricFogHalfTemporal (RGBA16F, half-res) - temporally reprojected fog
//     rgb = in-scattered radiance, a = transmittance
//   VolumetricFogHalfDepth   (R32F,  half-res)  - raw depth at each
//     half-res pixel's raymarch sample
//   DepthView                (float, full-res)   - scene depth
// Writes:
//   VolumetricFogColor       (RGBA16F, full-res) - bilateral-upsampled
//     volumetric to be sampled by PostProcess.fs.
//
// Algorithm: 2x2 depth-aware bilateral filter.
//   For each full-res output pixel:
//     1. Sample full-res raw depth D_full.
//     2. Convert UV to half-res texel space; gather the four surrounding
//        half-res taps (floor and floor+1 in x/y).
//     3. Weight each tap by a bilinear spatial factor (sub-pixel distance)
//        multiplied by a Gaussian on |D_half_tap - D_full| in eye-linear
//        space.
//     4. If the total weight is below a floor (all four taps failed the
//        depth threshold), fall back to the closest-depth tap.
//
// Depth comparison runs in eye-linear distance derived from raw depth via the
// inverse projection, so the bilateral threshold is scene-scale invariant.
//
// Before the 2x2 filter, the full-res pixel ray is tested against the current
// fog OBB set. This keeps half-res taps from bleeding fog outside the authored
// bounds at the projected silhouette.

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D VolumetricFogHalfTemporal;
uniform sampler2D VolumetricFogHalfDepth;
uniform sampler2D DepthView;

uniform vec3 CameraPosition;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform float RenderTime;
uniform int DepthMode;
uniform int VolumetricFogDebugMode;

const int MaxVolumetricFogVolumes = 4;

struct VolumetricFogStruct
{
    bool Enabled;
    float Intensity;
    float MaxDistance;
    float StepSize;
    float JitterStrength;
    int VolumeCount;
};
uniform VolumetricFogStruct VolumetricFog;
uniform mat4 VolumetricFogWorldToLocal[MaxVolumetricFogVolumes];
uniform vec4 VolumetricFogHalfExtentsEdgeFade[MaxVolumetricFogVolumes];
uniform vec4 VolumetricFogNoiseScaleThreshold[MaxVolumetricFogVolumes];
uniform vec4 VolumetricFogNoiseOffsetAmount[MaxVolumetricFogVolumes];
uniform vec4 VolumetricFogNoiseVelocity[MaxVolumetricFogVolumes];

const vec3 VolumetricFogNoiseDomainOffset = vec3(17.37f, 41.13f, 29.91f);

float saturate(float value)
{
    return clamp(value, 0.0f, 1.0f);
}

float hash13(vec3 p)
{
    p = fract(p * 0.1031f);
    p += dot(p, p.zyx + 31.32f);
    return fract((p.x + p.y) * p.z);
}

float valueNoise(vec3 p)
{
    vec3 cell = floor(p);
    vec3 local = fract(p);
    vec3 smoothLocal = local * local * (3.0f - 2.0f * local);

    float n000 = hash13(cell + vec3(0.0f, 0.0f, 0.0f));
    float n100 = hash13(cell + vec3(1.0f, 0.0f, 0.0f));
    float n010 = hash13(cell + vec3(0.0f, 1.0f, 0.0f));
    float n110 = hash13(cell + vec3(1.0f, 1.0f, 0.0f));
    float n001 = hash13(cell + vec3(0.0f, 0.0f, 1.0f));
    float n101 = hash13(cell + vec3(1.0f, 0.0f, 1.0f));
    float n011 = hash13(cell + vec3(0.0f, 1.0f, 1.0f));
    float n111 = hash13(cell + vec3(1.0f, 1.0f, 1.0f));

    float nx00 = mix(n000, n100, smoothLocal.x);
    float nx10 = mix(n010, n110, smoothLocal.x);
    float nx01 = mix(n001, n101, smoothLocal.x);
    float nx11 = mix(n011, n111, smoothLocal.x);
    float nxy0 = mix(nx00, nx10, smoothLocal.y);
    float nxy1 = mix(nx01, nx11, smoothLocal.y);
    return mix(nxy0, nxy1, smoothLocal.z);
}

float fbm3(vec3 p)
{
    float sum = 0.0f;
    float amplitude = 0.6f;
    float frequency = 1.0f;
    for (int octave = 0; octave < 3; ++octave)
    {
        sum += valueNoise(p * frequency) * amplitude;
        frequency *= 2.02f;
        amplitude *= 0.5f;
    }
    return sum;
}

float SampleVolumeNoise01(int index, vec3 localPos, out float noiseAmount)
{
    vec4 noiseParams = VolumetricFogNoiseScaleThreshold[index];
    noiseAmount = saturate(noiseParams.z);
    if (noiseParams.x <= 0.0f || noiseAmount <= 0.0f)
        return 1.0f;

    vec3 noiseSamplePos = localPos * noiseParams.x
        + VolumetricFogNoiseOffsetAmount[index].xyz
        + VolumetricFogNoiseVelocity[index].xyz * RenderTime
        + VolumetricFogNoiseDomainOffset;
    return clamp(fbm3(noiseSamplePos), 0.0f, 1.0f);
}

float ResolveDepth(float d)
{
    return DepthMode == 1 ? (1.0f - d) : d;
}

// Returns eye-space linear distance for the given raw depth sample at UV.
// We reconstruct the view-space Z magnitude from the inverse projection so
// the bilateral weight is invariant to perspective.
float LinearEyeDistance(float rawDepth, vec2 uv)
{
    vec4 clip = vec4(vec3(uv, rawDepth) * 2.0f - 1.0f, 1.0f);
    vec4 view = InverseProjMatrix * clip;
    float w = max(abs(view.w), 1e-5f);
    return abs(view.z / w);
}

vec3 WorldPosFromDepthRaw(float rawDepth, vec2 uv)
{
    vec4 clip = vec4(vec3(uv, rawDepth) * 2.0f - 1.0f, 1.0f);
    vec4 view = InverseProjMatrix * clip;
    view /= max(abs(view.w), 1e-5f) * sign(view.w == 0.0f ? 1.0f : view.w);
    return (InverseViewMatrix * view).xyz;
}

bool IntersectVolumeOBB(int index, vec3 rayOriginWS, vec3 rayDirWS, out float tNear, out float tFar)
{
    mat4 worldToLocal = VolumetricFogWorldToLocal[index];
    vec3 localOrigin = (worldToLocal * vec4(rayOriginWS, 1.0f)).xyz;
    vec3 localDir = (worldToLocal * vec4(rayDirWS, 0.0f)).xyz;
    vec3 halfExtents = VolumetricFogHalfExtentsEdgeFade[index].xyz;

    vec3 safeDir = mix(localDir, vec3(1e-5f), lessThan(abs(localDir), vec3(1e-5f)));
    vec3 invDir = 1.0f / safeDir;
    vec3 t0 = (-halfExtents - localOrigin) * invDir;
    vec3 t1 = ( halfExtents - localOrigin) * invDir;
    vec3 tMinV = min(t0, t1);
    vec3 tMaxV = max(t0, t1);
    tNear = max(max(tMinV.x, tMinV.y), tMinV.z);
    tFar  = min(min(tMaxV.x, tMaxV.y), tMaxV.z);
    return tFar >= max(tNear, 0.0f);
}

float ComputeRayIntervalFade(int index, vec3 rayDirWS, float sampleT, float tNear, float tFar, bool fadeRayEntry, bool fadeRayExit, float noiseValue, float noiseAmount)
{
    if (tFar <= tNear)
        return 0.0f;

    float edgeFade = max(VolumetricFogHalfExtentsEdgeFade[index].w, 0.0f);
    if (edgeFade <= 0.0001f)
        return 1.0f;
    if (!fadeRayEntry && !fadeRayExit)
        return 1.0f;

    vec3 localRayDir = (VolumetricFogWorldToLocal[index] * vec4(rayDirWS, 0.0f)).xyz;
    float edgeFadeOnRay = edgeFade / max(length(localRayDir), 1e-5f);
    float distanceToEntry = fadeRayEntry ? sampleT - tNear : edgeFadeOnRay;
    float distanceToExit = fadeRayExit ? tFar - sampleT : edgeFadeOnRay;
    float distanceToRayBounds = max(min(distanceToEntry, distanceToExit), 0.0f);
    float edgeErosion = edgeFadeOnRay * 0.85f * saturate(noiseAmount) * (1.0f - clamp(noiseValue, 0.0f, 1.0f));
    float noisyDistance = max(distanceToRayBounds - edgeErosion, 0.0f);
    return smoothstep(0.0f, edgeFadeOnRay, noisyDistance);
}

float ViewRayFogFade(float rawDepth, float resolvedDepth, vec2 uv)
{
    if (!VolumetricFog.Enabled || VolumetricFog.VolumeCount <= 0 || VolumetricFog.Intensity <= 0.0f || VolumetricFog.MaxDistance <= 0.0f)
        return 0.0f;

    float rayLength = VolumetricFog.MaxDistance;
    float farRawDepth = DepthMode == 1 ? 0.0f : 1.0f;
    vec3 rayEnd = resolvedDepth >= 0.999999f
        ? WorldPosFromDepthRaw(farRawDepth, uv)
        : WorldPosFromDepthRaw(rawDepth, uv);
    vec3 rayVector = rayEnd - CameraPosition;
    float rayVectorLength = length(rayVector);
    if (rayVectorLength <= 1e-5f)
        return 0.0f;

    if (resolvedDepth < 0.999999f)
        rayLength = min(rayLength, rayVectorLength);
    if (rayLength <= 0.0f)
        return 0.0f;

    vec3 rayDir = rayVector / rayVectorLength;
    float bestFade = 0.0f;
    for (int volumeIndex = 0; volumeIndex < VolumetricFog.VolumeCount; ++volumeIndex)
    {
        float tNear, tFar;
        if (!IntersectVolumeOBB(volumeIndex, CameraPosition, rayDir, tNear, tFar))
            continue;

        bool fadeRayEntry = tNear > 0.0f;
        bool fadeRayExit = tFar <= rayLength + 1e-4f;
        tNear = max(tNear, 0.0f);
        tFar = min(tFar, rayLength);
        if (tFar > tNear)
        {
            float sampleT = (tNear + tFar) * 0.5f;
            vec3 samplePosWS = CameraPosition + rayDir * sampleT;
            vec3 localPos = (VolumetricFogWorldToLocal[volumeIndex] * vec4(samplePosWS, 1.0f)).xyz;
            float noiseAmount;
            float noiseValue = SampleVolumeNoise01(volumeIndex, localPos, noiseAmount);
            bestFade = max(bestFade, ComputeRayIntervalFade(volumeIndex, rayDir, sampleT, tNear, tFar, fadeRayEntry, fadeRayExit, noiseValue, noiseAmount));
        }
    }

    return bestFade;
}

vec4 ApplyFogOutputFade(vec4 fog, float fade)
{
    float clampedFade = clamp(fade, 0.0f, 1.0f);
    return vec4(fog.rgb * clampedFade, mix(1.0f, fog.a, clampedFade));
}

void main()
{
    vec2 ndc = FragPos.xy;
    if (ndc.x > 1.0f || ndc.y > 1.0f)
        discard;
    vec2 uv = ndc * 0.5f + 0.5f;

    float rawFullDepth = texture(DepthView, uv).r;
    float resolvedFullDepth = ResolveDepth(rawFullDepth);

    float volumeFade = VolumetricFogDebugMode == 0
        ? ViewRayFogFade(rawFullDepth, resolvedFullDepth, uv)
        : 1.0f;
    if (volumeFade <= 0.0f)
    {
        OutColor = vec4(0.0f, 0.0f, 0.0f, 1.0f);
        return;
    }

    float linearFull = LinearEyeDistance(rawFullDepth, uv);

    // Locate the four surrounding half-res taps in texel space.
    vec2 halfSize = vec2(textureSize(VolumetricFogHalfTemporal, 0));
    if (halfSize.x <= 0.0f || halfSize.y <= 0.0f)
    {
        OutColor = ApplyFogOutputFade(texture(VolumetricFogHalfTemporal, uv), volumeFade);
        return;
    }

    vec2 halfTexel = 1.0f / halfSize;
    // Shift by -0.5 texel so uv * halfSize - 0.5 gives fractional offset
    // relative to the four nearest half-res pixel centers.
    vec2 halfPix = uv * halfSize - 0.5f;
    vec2 halfBase = floor(halfPix);
    vec2 halfFrac = halfPix - halfBase;

    // Clamp the base so the four taps stay in range even at the edge.
    halfBase = clamp(halfBase, vec2(0.0f), halfSize - 2.0f);

    // Bilinear spatial weights for the 2x2 neighborhood.
    vec4 spatial = vec4(
        (1.0f - halfFrac.x) * (1.0f - halfFrac.y),
        (       halfFrac.x) * (1.0f - halfFrac.y),
        (1.0f - halfFrac.x) * (       halfFrac.y),
        (       halfFrac.x) * (       halfFrac.y));

    // Depth threshold in eye-linear units. Scales with far-plane distance
    // so we don't over-reject at long range. 2% of current-pixel distance
    // plus a small epsilon keeps near-plane pixels from collapsing to
    // nearest-only on minor depth variance.
    float sigma = max(linearFull * 0.02f, 0.05f);

    vec4 accum = vec4(0.0f);
    float weightSum = 0.0f;

    // Track the nearest-depth tap as a fallback in case all taps are
    // rejected (e.g. full-res pixel sits across a silhouette edge from
    // every half-res sample).
    float bestDelta = 1.0e30f;
    vec4 bestColor = vec4(0.0f, 0.0f, 0.0f, 1.0f);

    for (int j = 0; j < 2; ++j)
    {
        for (int i = 0; i < 2; ++i)
        {
            int index = j * 2 + i;
            vec2 tapUV = (halfBase + vec2(float(i) + 0.5f, float(j) + 0.5f)) * halfTexel;
            vec4 tapColor = textureLod(VolumetricFogHalfTemporal, tapUV, 0.0f);
            float tapRaw = textureLod(VolumetricFogHalfDepth, tapUV, 0.0f).r;
            float tapLinear = LinearEyeDistance(tapRaw, tapUV);

            float delta = abs(tapLinear - linearFull);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestColor = tapColor;
            }

            // Gaussian on linear-depth distance.
            float depthW = exp(-(delta * delta) / (2.0f * sigma * sigma));
            float w = spatial[index] * depthW;

            accum += tapColor * w;
            weightSum += w;
        }
    }

    // If nothing survived the depth rejection, fall back to the nearest tap.
    if (weightSum < 1e-4f)
    {
        OutColor = ApplyFogOutputFade(bestColor, volumeFade);
        return;
    }

    OutColor = ApplyFogOutputFade(accum / weightSum, volumeFade);
}
