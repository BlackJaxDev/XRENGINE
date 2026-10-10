// Half-resolution GTAO visibility. The sampled depth is WebGPU zero-to-one
// depth; framebuffer UV is top-down and clip Y is up. Power is applied by the
// forward ambient consumer, not by this generator.
static const float PI = 3.14159265359;
static const float HALF_PI = 1.57079632679;

struct GTAOUniforms
{
    float4x4 viewMatrix;
    float4x4 inverseProjMatrix;
    float4x4 projMatrix;
    float2 outputSize;
    float2 depthSize;
    float radius;
    float bias;
    float falloffStartRatio;
    float thicknessHeuristic;
    float visibilityBitmaskThickness;
    int sliceCount;
    int stepsPerSlice;
    int useInputNormals;
    int useVisibilityBitmask;
    int depthMode;
    float2 outputOrigin;
};

[[vk::binding(0, 0)]] ConstantBuffer<GTAOUniforms> gtao;
[[vk::binding(0, 1)]] DepthTexture2D depthView;
[[vk::binding(0, 2)]] Texture2D<float4> normalTexture;
[[vk::binding(1, 2)]] SamplerState normalSampler;

struct GTAOVertexInput { [[vk::location(0)]] float3 position : POSITION; };

[shader("vertex")]
float4 gtaoVertex(GTAOVertexInput input) : SV_Position
{
    return float4(input.position.xy, 0.0, 1.0);
}

float fastAcos(float x)
{
    float ax = abs(x);
    float r = (-0.0187293 * ax + 0.0742610) * ax - 0.2121144;
    r = (r * ax + 1.5707288) * sqrt(max(1.0 - ax, 0.0));
    return x < 0.0 ? PI - r : r;
}

float readDepth(float2 uv)
{
    int2 texel = int2(clamp(uv * gtao.depthSize, float2(0.0), gtao.depthSize - 1.0));
    return depthView.Load(int3(texel, 0));
}

bool isFarDepth(float depth)
{
    return gtao.depthMode == 1 ? depth <= 0.000001 : depth >= 0.999999;
}

float3 viewPosFromDepth(float depth, float2 uv)
{
    float4 clip = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0);
    float4 view = mul(gtao.inverseProjMatrix, clip);
    return view.xyz / max(view.w, 0.00001);
}

float3 decodeNormal(float2 encoded)
{
    float2 f = encoded * 2.0 - 1.0;
    float3 n = float3(f, 1.0 - abs(f.x) - abs(f.y));
    float t = clamp(-n.z, 0.0, 1.0);
    n.xy -= float2(n.x >= 0.0 ? t : -t, n.y >= 0.0 ? t : -t);
    return normalize(n);
}

float3 getViewNormal(float2 uv, float3 centerPos)
{
    if (gtao.useInputNormals != 0)
    {
        float3 worldNormal = decodeNormal(normalTexture.SampleLevel(normalSampler, uv, 0.0).xy);
        return normalize(mul(gtao.viewMatrix, float4(worldNormal, 0.0)).xyz);
    }

    float2 texelSize = 1.0 / gtao.depthSize;
    float2 uvX = clamp(uv + float2(texelSize.x, 0.0), float2(0.0), float2(1.0));
    float2 uvY = clamp(uv + float2(0.0, texelSize.y), float2(0.0), float2(1.0));
    float depthX = readDepth(uvX);
    float depthY = readDepth(uvY);
    if (isFarDepth(depthX) || isFarDepth(depthY))
        return float3(0.0, 0.0, 1.0);
    float3 posX = viewPosFromDepth(depthX, uvX);
    float3 posY = viewPosFromDepth(depthY, uvY);
    // A positive texture-Y step points down while the reconstructed clip Y
    // points up. Match the desktop normal-reconstruction handedness.
    return normalize(cross(posX - centerPos, centerPos - posY));
}

float interleavedGradientNoise(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

float edgeFade(float2 uv, float radiusPixels)
{
    float2 edgePixels = min(uv, 1.0 - uv) * gtao.depthSize;
    float nearestEdge = min(edgePixels.x, edgePixels.y);
    float fadePixels = clamp(radiusPixels * 0.08, 2.0, 8.0);
    float t = saturate(nearestEdge / fadePixels);
    return t * t * (3.0 - 2.0 * t);
}

float integrateArc(float h, float n)
{
    float hc = clamp(h, 0.0, HALF_PI);
    float nc = clamp(n, -HALF_PI, HALF_PI);
    return 0.25 * (-cos(2.0 * hc - nc) + cos(nc) + 2.0 * hc * sin(nc));
}

uint updateSectors(float minHorizon, float maxHorizon, uint bits)
{
    float low = clamp(min(minHorizon, maxHorizon), 0.0, 1.0);
    float high = clamp(max(minHorizon, maxHorizon), 0.0, 1.0);
    if (high <= low)
        return bits;
    uint start = min(uint(low * 32.0), 31u);
    uint span = max(uint(ceil((high - low) * 32.0)), 1u);
    span = min(span, 32u - start);
    uint range = span >= 32u ? 0xffffffffu : (0xffffffffu >> (32u - span));
    return bits | (range << start);
}

uint accumulateSectors(float3 delta, float3 viewDir, float normalAngle,
    float direction, float thickness, uint bits)
{
    if (dot(delta, delta) <= 0.00000001)
        return bits;
    float3 backface = delta - viewDir * thickness;
    if (dot(backface, backface) <= 0.00000001)
        return bits;
    float2 horizon = float2(
        fastAcos(clamp(dot(normalize(delta), viewDir), -1.0, 1.0)),
        fastAcos(clamp(dot(normalize(backface), viewDir), -1.0, 1.0)));
    horizon = saturate(((direction * -horizon) - normalAngle + HALF_PI) / PI);
    if (direction >= 0.0)
        horizon = horizon.yx;
    float biasOffset = clamp(gtao.bias / PI, 0.0, 0.499);
    return updateSectors(min(horizon.x, horizon.y) + biasOffset,
        max(horizon.x, horizon.y) - biasOffset, bits);
}

float sampleFalloff(float3 delta, float dist, float3 viewDir,
    float falloffStart, float radius, float thicknessLimit)
{
    float t = saturate((dist - falloffStart) / max(radius - falloffStart, 0.00001));
    float falloff = 1.0 - t * t * (3.0 - 2.0 * t);
    float behind = max(0.0, -dot(delta, viewDir));
    if (thicknessLimit > 0.0)
    {
        float q = saturate(behind / thicknessLimit);
        falloff *= 1.0 - q * q * (3.0 - 2.0 * q);
    }
    return saturate(falloff);
}

void accumulateSample(float2 sampleUV, float3 centerPos, float3 viewDir,
    float3 slicePlaneN, float normalAngle, float direction, float radius,
    float falloffStart, float thicknessLimit, float bitmaskThickness,
    inout uint occludedSectors, inout float occludedWeight, inout float horizon)
{
    if (any(sampleUV <= float2(0.0)) || any(sampleUV >= float2(1.0)))
        return;
    float depth = readDepth(sampleUV);
    if (isFarDepth(depth))
        return;
    float3 delta = viewPosFromDepth(depth, sampleUV) - centerPos;
    float dist = length(delta);
    if (dist <= 0.0001 || dist > radius)
        return;
    float falloff = sampleFalloff(delta, dist, viewDir, falloffStart, radius, thicknessLimit);
    if (gtao.useVisibilityBitmask != 0)
    {
        if (falloff <= 0.01)
            return;
        uint previous = occludedSectors;
        uint updated = accumulateSectors(delta, viewDir, normalAngle,
            direction, bitmaskThickness * falloff, previous);
        occludedWeight += float(countbits(updated & ~previous)) * falloff;
        occludedSectors = updated;
    }
    else
    {
        float3 deltaDir = delta / dist;
        float3 projected = deltaDir - slicePlaneN * dot(deltaDir, slicePlaneN);
        float projectedLength = length(projected);
        if (projectedLength <= 0.00001)
            return;
        projected /= projectedLength;
        float candidate = fastAcos(clamp(dot(projected, viewDir), 0.0, 1.0));
        candidate = lerp(HALF_PI, candidate, falloff);
        horizon = min(horizon, clamp(candidate + gtao.bias, 0.0, HALF_PI));
    }
}

[shader("fragment")]
float4 gtaoFragment(float4 position : SV_Position) : SV_Target0
{
    float2 uv = saturate((position.xy - gtao.outputOrigin) /
        max(gtao.outputSize, float2(1.0)));
    float depth = readDepth(uv);
    if (isFarDepth(depth))
        return float4(1.0, 1.0, 1.0, 1.0);

    float3 centerPos = viewPosFromDepth(depth, uv);
    float3 centerNormal = getViewNormal(uv, centerPos);
    float3 viewDir = normalize(-centerPos);
    float radius = max(gtao.radius, 0.001);
    float radiusPixels = clamp(radius * abs(gtao.projMatrix[1][1]) * 0.5 * gtao.depthSize.y /
        max(abs(centerPos.z), 0.001), 1.0, 192.0);
    float falloffStart = saturate(gtao.falloffStartRatio) * radius;
    float thicknessLimit = radius * saturate(gtao.thicknessHeuristic);
    float bitmaskThickness = max(gtao.visibilityBitmaskThickness, 0.0001);
    int slices = clamp(gtao.sliceCount, 1, 8);
    int steps = clamp(gtao.stepsPerSlice, 1, 16);
    float2 noisePixel = floor(position.xy - gtao.outputOrigin);
    float baseAngle = (interleavedGradientNoise(noisePixel) - 0.5) * PI / float(slices);
    float jitter = interleavedGradientNoise(noisePixel + float2(17.0, 43.0));
    float visibility = 0.0;

    for (int sliceIndex = 0; sliceIndex < slices; ++sliceIndex)
    {
        float phi = baseAngle + PI * float(sliceIndex) / float(slices);
        float2 screenDir = float2(cos(phi), sin(phi));
        float3 tangent = normalize(float3(screenDir.x / gtao.projMatrix[0][0],
            -screenDir.y / gtao.projMatrix[1][1], 0.0));
        tangent = normalize(tangent - viewDir * dot(tangent, viewDir));
        float3 slicePlaneN = normalize(cross(viewDir, tangent));
        float3 projectedNormal = centerNormal - slicePlaneN * dot(centerNormal, slicePlaneN);
        float projectedNormalLength = length(projectedNormal);
        if (projectedNormalLength <= 0.00001)
        {
            visibility += 1.0;
            continue;
        }
        projectedNormal /= projectedNormalLength;
        float gamma = atan2(dot(projectedNormal, tangent), dot(projectedNormal, viewDir));
        float normalAngle = -gamma;
        float horizonForward = HALF_PI;
        float horizonBackward = HALF_PI;
        uint sectors = 0u;
        float sectorWeight = 0.0;

        for (int step = 0; step < steps; ++step)
        {
            float t = (float(step) + jitter) / float(steps);
            float2 offset = screenDir * radiusPixels * t / gtao.depthSize;
            accumulateSample(uv + offset, centerPos, viewDir, slicePlaneN, normalAngle,
                1.0, radius, falloffStart, thicknessLimit, bitmaskThickness,
                sectors, sectorWeight, horizonForward);
            accumulateSample(uv - offset, centerPos, viewDir, slicePlaneN, normalAngle,
                -1.0, radius, falloffStart, thicknessLimit, bitmaskThickness,
                sectors, sectorWeight, horizonBackward);
        }
        float sliceVisibility = gtao.useVisibilityBitmask != 0
            ? 1.0 - sectorWeight / 32.0
            : projectedNormalLength * (integrateArc(horizonForward, gamma) +
                integrateArc(horizonBackward, -gamma));
        visibility += saturate(sliceVisibility);
    }

    visibility = saturate(visibility / float(slices));
    visibility = lerp(1.0, visibility, edgeFade(uv, radiusPixels));
    return float4(visibility, visibility, visibility, 1.0);
}
