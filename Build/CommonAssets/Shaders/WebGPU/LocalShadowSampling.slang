// One projected spot map and one six-face radial point cube. These bounded
// receivers preserve the desktop forward texel-relative bias and PCSS math.
struct LocalShadowUniforms
{
    float4x4 spotViewProjection;
    float4 spotControl;       // enabled, packed light index, near, far
    float4 spotPosition;
    float4 spotDirection;     // direction, outer cutoff cosine
    float4 spotBias;          // depth, slope, normal bias texels
    float4 spotFilter;        // blocker search, filter radius, min/max penumbra
    float4 spotSource;        // effective light source radius
    float4 pointControl;      // enabled, packed light index, near, far
    float4 pointPosition;
    float4 pointBias;
    float4 pointFilter;
    float4 pointSource;
};
[[vk::binding(3, 3)]] ConstantBuffer<LocalShadowUniforms> localShadow;
[[vk::binding(4, 3)]] Texture2D<float> spotShadowMap;
[[vk::binding(5, 3)]] SamplerState spotShadowSampler;
[[vk::binding(6, 3)]] TextureCube<float> pointShadowMap;
[[vk::binding(7, 3)]] SamplerState pointShadowSampler;

float localShadowRotation(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715)))) * (2.0 * ShadowPi);
}

float selectedSpotVisibility(int index, float3 position, float3 normal, float2 pixel)
{
    if (localShadow.spotControl.x <= 0.5 || int(localShadow.spotControl.y) != index)
        return 1.0;
    uint width;
    uint height;
    spotShadowMap.GetDimensions(width, height);
    float2 size = float2(width, height);
    float texel = 1.0 / max(float(width), 1.0);
    float axisDistance = max(dot(position - localShadow.spotPosition.xyz,
        safeNormalize(localShadow.spotDirection.xyz)), 0.0001);
    float cosOuter = clamp(localShadow.spotDirection.w, 0.001, 0.999999);
    float tanOuter = sqrt(max(1.0 - cosOuter * cosOuter, 0.0)) / cosOuter;
    float worldTexel = 2.0 * axisDistance * tanOuter * max(texel, 1e-7);
    float3 offsetPosition = position + normal * worldTexel * max(localShadow.spotBias.z, 0.0);
    float4 clip = mul(localShadow.spotViewProjection, float4(offsetPosition, 1.0));
    float3 ndc = clip.xyz / clip.w;
    float3 coord = float3(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5, ndc.z);
    float2 uvDx = ddx(coord.xy);
    float2 uvDy = ddy(coord.xy);
    float depthDx = ddx(coord.z);
    float depthDy = ddy(coord.z);
    float determinant = uvDx.x * uvDy.y - uvDx.y * uvDy.x;
    float slopeBias = 0.0;
    if (abs(determinant) > 1e-8)
    {
        float2 gradient = float2(uvDy.y * depthDx - uvDx.y * depthDy,
            -uvDy.x * depthDx + uvDx.x * depthDy) / determinant;
        slopeBias = dot(abs(gradient), float2(max(localShadow.spotFilter.y, texel))) * max(localShadow.spotBias.y, 0.0);
    }
    float nearZ = max(localShadow.spotControl.z, 0.0001);
    float farZ = max(localShadow.spotControl.w, nearZ + 0.0001);
    float z = max(axisDistance, nearZ + 0.0001);
    float constantBias = max(worldTexel * localShadow.spotBias.x, 0.0) * nearZ * farZ /
        max((farZ - nearZ) * z * z, 1e-8);
    float receiverDepth = coord.z - (constantBias + max(slopeBias, 0.0));
    // Derivatives are evaluated above the fragment-varying frustum rejection.
    if (clip.w <= 0.0 || axisDistance >= farZ || any(coord < float3(0.0)) || any(coord > float3(1.0)))
        return 1.0;
    float rotation = localShadowRotation(pixel);
    float searchRadius = max(localShadow.spotFilter.x, max(1.0 / size.x, 1.0 / size.y));
    float blockerSum = 0.0;
    int blockerCount = 0;
    [unroll]
    for (int tap = 0; tap < 8; tap++)
    {
        float2 uv = coord.xy + vogelTap(tap, rotation) * searchRadius;
        int2 texelPosition = int2(clamp(uv * size, float2(0.0), size - 1.0));
        float depth = spotShadowMap.Load(int3(texelPosition, 0));
        if (depth < receiverDepth) { blockerSum += depth; blockerCount++; }
    }
    if (blockerCount == 0) return 1.0;
    float averageBlocker = blockerSum / float(blockerCount);
    float minRadius = max(localShadow.spotFilter.z, max(1.0 / size.x, 1.0 / size.y));
    float maxRadius = max(localShadow.spotFilter.w, minRadius);
    float penumbra = abs(receiverDepth - averageBlocker) / max(abs(averageBlocker), 0.0001) * max(localShadow.spotSource.x, 0.0);
    float filterRadius = max(clamp(penumbra, minRadius, maxRadius), max(1.0 / size.x, 1.0 / size.y));
    float visibility = 0.0;
    [unroll]
    for (int tap = 0; tap < 8; tap++)
        visibility += receiverDepth <= spotShadowMap.SampleLevel(spotShadowSampler,
            coord.xy + vogelTap(tap, rotation) * filterRadius, 0.0) ? 1.0 : 0.0;
    return visibility * 0.125;
}

float selectedPointVisibility(int index, float3 position, float3 normal, float2 pixel)
{
    if (localShadow.pointControl.x <= 0.5 || int(localShadow.pointControl.y) != index)
        return 1.0;
    uint width;
    uint height;
    pointShadowMap.GetDimensions(width, height);
    float directionTexel = 2.0 / max(float(width), 1.0);
    float3 lightToReceiver = position - localShadow.pointPosition.xyz;
    float receiverDistance = length(lightToReceiver);
    float farZ = localShadow.pointControl.w;
    float3 lightDirection = receiverDistance > 1e-6 ? -lightToReceiver / receiverDistance : float3(0.0, 0.0, 1.0);
    float noL = max(dot(normal, lightDirection), 0.0);
    float normalOffset = receiverDistance * directionTexel * max(localShadow.pointBias.z, 0.0);
    float3 shadowDirection = position + normal * normalOffset - localShadow.pointPosition.xyz;
    float distance = length(shadowDirection);
    if (receiverDistance >= farZ || distance >= farZ || distance <= localShadow.pointControl.z + normalOffset)
        return 1.0;
    float noLSafe = max(noL, 0.05);
    float tanTheta = sqrt(max(1.0 - noLSafe * noLSafe, 0.0)) / noLSafe;
    float filterTexels = max(1.0, clamp(localShadow.pointFilter.y * 256.0, 0.0, 8.0));
    float texelRelativeBias = directionTexel * (max(localShadow.pointBias.x, 0.0) +
        max(localShadow.pointBias.y, 0.0) * tanTheta * filterTexels);
    float receiverDepth = distance * (1.0 - max(texelRelativeBias, 1.0 / 512.0));
    float searchRadius = directionTexel * max(1.0, clamp(localShadow.pointFilter.x * 256.0, 0.0, 4.0));
    float minRadius = directionTexel * max(1.0, clamp(localShadow.pointFilter.z * 256.0, 0.0, 4.0));
    float maxRadius = directionTexel * max(1.0, clamp(localShadow.pointFilter.w * 256.0, 0.0, 4.0));
    float3 baseDirection = safeNormalize(shadowDirection);
    float3 up = abs(baseDirection.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
    float3 tangent = safeNormalize(cross(up, baseDirection));
    float3 bitangent = cross(baseDirection, tangent);
    float rotation = localShadowRotation(pixel);
    float blockerSum = 0.0;
    int blockerCount = 0;
    [unroll]
    for (int tap = 0; tap < 8; tap++)
    {
        float2 disk = vogelTap(tap, rotation) * max(searchRadius, 0.000001);
        float3 direction = safeNormalize(baseDirection + tangent * disk.x + bitangent * disk.y);
        float depth = pointShadowMap.SampleLevel(pointShadowSampler, direction, 0.0) * farZ;
        if (depth < receiverDepth) { blockerSum += depth; blockerCount++; }
    }
    if (blockerCount == 0) return 1.0;
    float averageBlocker = blockerSum / float(blockerCount);
    float angularSourceRadius = max(localShadow.pointSource.x, 0.0) / max(averageBlocker, 0.0001);
    float penumbra = (receiverDepth - averageBlocker) / max(averageBlocker, 0.0001) * angularSourceRadius;
    float radius = clamp(penumbra, max(minRadius, 0.000001), max(maxRadius, minRadius));
    float visibility = 0.0;
    [unroll]
    for (int tap = 0; tap < 8; tap++)
    {
        float2 disk = vogelTap(tap, rotation) * radius;
        float3 direction = safeNormalize(baseDirection + tangent * disk.x + bitangent * disk.y);
        float depth = pointShadowMap.SampleLevel(pointShadowSampler, direction, 0.0) * farZ;
        visibility += receiverDepth <= depth ? 1.0 : 0.0;
    }
    return visibility * 0.125;
}
