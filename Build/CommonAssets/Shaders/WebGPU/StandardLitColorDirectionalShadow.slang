// Opaque forward StandardLitColor with one standalone directional depth shadow.
// Direct lighting, no-probe ambient, and the bounded PCSS receiver follow the
// desktop snippets. The host owns a real orthographic light VP; this shader does
// not infer a sun transform. Cascades, atlas, contact shadows, other filter
// modes, and more than 4/8/8 direct lights require separate explicit variants.

struct LitViewUniforms
{
    float4x4 viewProjection;
    float4 cameraPosition;
};

struct LitObjectUniforms
{
    float4x4 modelMatrix;
    float4x4 normalMatrix;
};

struct LitMaterialUniforms
{
    float4 baseColorOpacity;
    float4 roughnessMetallicSpecularEmission;
#ifdef XRE_STANDARD_LIT_COVERAGE
    float4 coverage;
#endif
};

// Flat float4 fields keep the whole-program ABI explicit and portable without
// storage buffers, optional device features, or runtime-sized uniform arrays.
#define DIRECTIONAL_FIELDS(N) float4 directional##N##Direction; float4 directional##N##ColorIntensity;
#define POINT_FIELDS(N) float4 point##N##PositionRadius; float4 point##N##ColorIntensity; float4 point##N##Brightness;
#define SPOT_FIELDS(N) float4 spot##N##PositionRadius; float4 spot##N##ColorIntensity; float4 spot##N##DirectionExponent; float4 spot##N##CutoffsBrightness;

struct LitLightingUniforms
{
    float4 lightCounts;
    float4 globalAmbient;
    DIRECTIONAL_FIELDS(0)
    DIRECTIONAL_FIELDS(1)
    DIRECTIONAL_FIELDS(2)
    DIRECTIONAL_FIELDS(3)
    POINT_FIELDS(0)
    POINT_FIELDS(1)
    POINT_FIELDS(2)
    POINT_FIELDS(3)
    POINT_FIELDS(4)
    POINT_FIELDS(5)
    POINT_FIELDS(6)
    POINT_FIELDS(7)
    SPOT_FIELDS(0)
    SPOT_FIELDS(1)
    SPOT_FIELDS(2)
    SPOT_FIELDS(3)
    SPOT_FIELDS(4)
    SPOT_FIELDS(5)
    SPOT_FIELDS(6)
    SPOT_FIELDS(7)
    float4 ambientOcclusionControls; // enabled, power, multi-bounce, reserved
};

// The 144-byte View-frequency block is kept separate from the stable lighting
// ABI. The host selects one of the four packed directional entries and uploads
// its actual shadow-camera VP and projection-derived bias values.
struct DirectionalShadowUniforms
{
    float4x4 lightViewProjection;
    float4 control;          // enabled, packed directional index, reserved, reserved
    float4 biasProjection;   // constant depth bias, normal offset WS, texel WS, depth range
    float4 biasParams;       // depth, slope, normal bias texels, reserved
    float4 filterParams;     // blocker search, filter radius, min/max penumbra (UV)
    float4 sourceParams;     // light source radius, authored min/max bias, reserved
};

[[vk::binding(0, 0)]] ConstantBuffer<LitViewUniforms> view;
[[vk::binding(1, 0)]] ConstantBuffer<LitObjectUniforms> object;
[[vk::binding(0, 1)]] ConstantBuffer<LitMaterialUniforms> material;
[[vk::binding(0, 2)]] ConstantBuffer<LitLightingUniforms> lighting;
[[vk::binding(1, 2)]] Texture2D<float4> ambientOcclusionTexture;
[[vk::binding(2, 2)]] SamplerState ambientOcclusionSampler;
[[vk::binding(0, 3)]] ConstantBuffer<DirectionalShadowUniforms> directionalShadow;
[[vk::binding(1, 3)]] DepthTexture2D directionalShadowMap;
[[vk::binding(2, 3)]] SamplerComparisonState directionalShadowSampler;

struct LitVertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
};

struct LitVertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 worldPosition : TEXCOORD0;
    [[vk::location(1)]] float3 worldNormal : TEXCOORD1;
};

[shader("vertex")]
LitVertexOutput standardLitVertex(LitVertexInput input)
{
    LitVertexOutput output;
    float4 world = mul(object.modelMatrix, float4(input.position, 1.0));
    output.position = mul(view.viewProjection, world);
    output.worldPosition = world.xyz;
    output.worldNormal = mul(object.normalMatrix, float4(input.normal, 0.0)).xyz;
    return output;
}

float3 safeNormalize(float3 value)
{
    return value * rsqrt(max(dot(value, value), 1e-20));
}

float3 ambientVisibility(float2 framebufferPixel, float3 albedo)
{
    if (lighting.ambientOcclusionControls.x < 0.5)
        return float3(1.0);

    uint width;
    uint height;
    ambientOcclusionTexture.GetDimensions(width, height);
    float2 uv = clamp(framebufferPixel / max(float2(width, height), float2(1.0)),
        float2(0.0), float2(0.999999));
    float visibility = pow(saturate(ambientOcclusionTexture.SampleLevel(
        ambientOcclusionSampler, uv, 0.0).x), max(lighting.ambientOcclusionControls.y, 0.001));
    if (lighting.ambientOcclusionControls.z < 0.5)
        return float3(visibility);

    float3 a = 2.0404 * albedo - 0.3324;
    float3 b = -4.7951 * albedo + 0.6417;
    float3 c = 2.7552 * albedo + 0.6903;
    return max(float3(visibility), ((a * visibility + b) * visibility + c) * visibility);
}

float lightAttenuation(float distance, float radius)
{
    float relativeDistance = distance / max(radius, 1e-6);
    float relativeDistance2 = relativeDistance * relativeDistance;
    float falloff = saturate(1.0 - relativeDistance2 * relativeDistance2);
    return falloff * falloff / (distance * distance + 1.0);
}

float3 directPbr(float3 color, float intensity, float3 lightDirection,
    float3 normal, float3 viewDirection, float3 albedo, float3 rms, float attenuation)
{
    float noL = max(dot(normal, lightDirection), 0.0);
    if (noL <= 0.0)
        return float3(0.0);

    float3 halfVector = safeNormalize(viewDirection + lightDirection);
    float noH = max(dot(normal, halfVector), 0.0);
    float noV = max(dot(normal, viewDirection), 0.0);
    float hoV = max(dot(halfVector, viewDirection), 0.0);
    float a = rms.x * rms.x;
    float a2 = a * a;
    float distributionDenominator = noH * noH * (a2 - 1.0) + 1.0;
    // The desktop formula is singular at exactly zero roughness and NoH=1.
    // Bound only that denominator; ordinary authored roughness is unchanged.
    float distribution = a2 / max(3.14159265359 * distributionDenominator * distributionDenominator, 1e-20);
    float k = (rms.x + 1.0) * (rms.x + 1.0) * 0.125;
    float geometry = (noV / (noV * (1.0 - k) + k)) * (noL / (noL * (1.0 - k) + k));
    float3 f0 = lerp(float3(0.04), albedo, rms.y);
    float3 fresnel = f0 + (1.0 - f0) * exp2((-5.55473 * hoV - 6.98316) * hoV);
    float3 specular = rms.z * distribution * geometry * fresnel / (4.0 * noV * noL + 0.0001);
    float3 diffuseWeight = (1.0 - fresnel) * (1.0 - rms.y);
    return (diffuseWeight * albedo / 3.14159265359 + specular) * (attenuation * color * intensity) * noL;
}

// Bounded port of ShadowSampling.glsl's rotated Vogel blocker search, PCSS
// penumbra, receiver-plane depth bias, and Vogel filter. The depth texture is
// loaded without filtering for blockers; the filter uses hardware comparison.
static const float ShadowPi = 3.14159265359;
static const float VogelGoldenAngle = 2.39996323;

float2 vogelTap(int index, float rotation)
{
    float sampleIndex = float(index) + 0.5;
    float radius = sqrt(sampleIndex / 8.0);
    float angle = sampleIndex * VogelGoldenAngle + rotation;
    return radius * float2(cos(angle), sin(angle));
}

float sampleDirectionalShadow(float3 position, float3 normal, float2 pixel)
{
    uint mapWidth;
    uint mapHeight;
    directionalShadowMap.GetDimensions(mapWidth, mapHeight);
    float2 mapSize = float2(mapWidth, mapHeight);
    float texelUv = max(1.0 / mapSize.x, 1.0 / mapSize.y);
    float3 offsetPosition = position + normal * max(directionalShadow.biasProjection.y, 0.0);
    float4 lightClip = mul(directionalShadow.lightViewProjection, float4(offsetPosition, 1.0));
    float3 ndc = lightClip.xyz / lightClip.w;
    // WebGPU clip depth is already zero-to-one; its framebuffer UV origin is top-left.
    float3 shadowCoord = float3(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5, ndc.z);

    // Evaluate derivatives before the per-fragment bounds branch so the
    // receiver-plane estimate stays in uniform control flow.
    float2 uvDx = ddx(shadowCoord.xy);
    float2 uvDy = ddy(shadowCoord.xy);
    float depthDx = ddx(shadowCoord.z);
    float depthDy = ddy(shadowCoord.z);
    float determinant = uvDx.x * uvDy.y - uvDx.y * uvDy.x;
    float receiverPlaneBias = 0.0;
    if (abs(determinant) > 1e-8)
    {
        float2 depthGradientUv = float2(
            (uvDy.y * depthDx - uvDx.y * depthDy) / determinant,
            (-uvDy.x * depthDx + uvDx.x * depthDy) / determinant);
        float footprintUv = max(directionalShadow.filterParams.y, texelUv);
        receiverPlaneBias = dot(abs(depthGradientUv), float2(footprintUv, footprintUv))
            * max(directionalShadow.biasParams.y, 0.0);
    }
    // The desktop forward receiver uses the projection-derived constant plus
    // receiver-plane bias. Authored min/max legacy fields do not clamp it.
    float bias = max(directionalShadow.biasProjection.x, 0.0) + max(receiverPlaneBias, 0.0);
    float receiverDepth = shadowCoord.z - bias;

    if (any(shadowCoord < float3(0.0)) || any(shadowCoord > float3(1.0)))
        return 1.0;

    float rotation = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))))
        * (2.0 * ShadowPi);
    float searchRadius = max(directionalShadow.filterParams.x, texelUv);
    float blockerSum = 0.0;
    int blockerCount = 0;
    [unroll]
    for (int i = 0; i < 8; ++i)
    {
        float2 uv = shadowCoord.xy + vogelTap(i, rotation) * searchRadius;
        int2 texel = int2(clamp(uv * mapSize, float2(0.0), mapSize - 1.0));
        float sampleDepth = directionalShadowMap.Load(int3(texel, 0));
        if (sampleDepth < receiverDepth)
        {
            blockerSum += sampleDepth;
            blockerCount++;
        }
    }
    if (blockerCount == 0)
        return 1.0;

    float averageBlocker = blockerSum / float(blockerCount);
    float minRadius = max(directionalShadow.filterParams.z, texelUv);
    float maxRadius = max(directionalShadow.filterParams.w, minRadius);
    float penumbra = abs(receiverDepth - averageBlocker) / max(abs(averageBlocker), 0.0001)
        * max(directionalShadow.sourceParams.x, 0.0);
    float filterRadius = max(clamp(penumbra, minRadius, maxRadius), texelUv);
    float visibility = 0.0;
    [unroll]
    for (int i = 0; i < 8; ++i)
    {
        float2 uv = shadowCoord.xy + vogelTap(i, rotation) * filterRadius;
        visibility += directionalShadowMap.SampleCmpLevelZero(directionalShadowSampler, uv, receiverDepth);
    }
    return visibility * 0.125;
}

float selectedDirectionalVisibility(int index, float3 position, float3 normal, float2 pixel)
{
    if (directionalShadow.control.x <= 0.5 || int(directionalShadow.control.y) != index)
        return 1.0;
    return sampleDirectionalShadow(position, normal, pixel);
}

float3 directionalLight(float4 direction, float4 colorIntensity,
    float3 normal, float3 viewDirection, float3 albedo, float3 rms, float visibility)
{
    return directPbr(colorIntensity.xyz, colorIntensity.w, safeNormalize(-direction.xyz),
        normal, viewDirection, albedo, rms, visibility);
}

float3 pointLight(float4 positionRadius, float4 colorIntensity, float4 brightness,
    float3 position, float3 normal, float3 viewDirection, float3 albedo, float3 rms)
{
    float3 lightVector = positionRadius.xyz - position;
    float attenuation = lightAttenuation(length(lightVector), positionRadius.w) * brightness.x;
    return directPbr(colorIntensity.xyz, colorIntensity.w, safeNormalize(lightVector),
        normal, viewDirection, albedo, rms, attenuation);
}

float3 spotLight(float4 positionRadius, float4 colorIntensity, float4 directionExponent,
    float4 cutoffsBrightness, float3 position, float3 normal, float3 viewDirection, float3 albedo, float3 rms)
{
    float3 lightVector = positionRadius.xyz - position;
    float3 lightDirection = safeNormalize(lightVector);
    float cosine = max(0.0, dot(-lightDirection, safeNormalize(directionExponent.xyz)));
    // cutoffsBrightness = (inner cosine, outer cosine, brightness, unused).
    // Equal cone cutoffs have a defined hard edge instead of smoothstep(0/0).
    float cone = cutoffsBrightness.x == cutoffsBrightness.y
        ? (cosine >= cutoffsBrightness.x ? 1.0 : 0.0)
        : smoothstep(cutoffsBrightness.y, cutoffsBrightness.x, cosine);
    float coneAttenuation = pow(cosine, directionExponent.w);
    float attenuation = lightAttenuation(length(lightVector), positionRadius.w) * cutoffsBrightness.z;
    return cone * coneAttenuation * directPbr(colorIntensity.xyz, colorIntensity.w,
        lightDirection, normal, viewDirection, albedo, rms, attenuation);
}

#define ADD_DIRECTIONAL(N) if (lighting.lightCounts.x > N) color += directionalLight(lighting.directional##N##Direction, lighting.directional##N##ColorIntensity, normal, viewDirection, albedo, rms, selectedDirectionalVisibility(N, input.worldPosition, normal, input.position.xy));
#define ADD_POINT(N) if (lighting.lightCounts.y > N) color += pointLight(lighting.point##N##PositionRadius, lighting.point##N##ColorIntensity, lighting.point##N##Brightness, input.worldPosition, normal, viewDirection, albedo, rms);
#define ADD_SPOT(N) if (lighting.lightCounts.z > N) color += spotLight(lighting.spot##N##PositionRadius, lighting.spot##N##ColorIntensity, lighting.spot##N##DirectionExponent, lighting.spot##N##CutoffsBrightness, input.worldPosition, normal, viewDirection, albedo, rms);

[shader("fragment")]
float4 standardLitFragment(LitVertexOutput input) : SV_Target0
{
#ifdef XRE_STANDARD_LIT_COVERAGE
    if (material.coverage.x > 0.5 && material.baseColorOpacity.w < material.coverage.y)
        discard;
#endif
    float3 normal = safeNormalize(input.worldNormal);
    float3 viewDirection = safeNormalize(view.cameraPosition.xyz - input.worldPosition);
    float3 albedo = material.baseColorOpacity.xyz;
    float4 surface = material.roughnessMetallicSpecularEmission;
    float3 rms = float3(saturate(surface.x), saturate(surface.y), max(surface.z, 0.0));
    float3 color = float3(0.0);
    ADD_DIRECTIONAL(0)
    ADD_DIRECTIONAL(1)
    ADD_DIRECTIONAL(2)
    ADD_DIRECTIONAL(3)
    ADD_POINT(0)
    ADD_POINT(1)
    ADD_POINT(2)
    ADD_POINT(3)
    ADD_POINT(4)
    ADD_POINT(5)
    ADD_POINT(6)
    ADD_POINT(7)
    ADD_SPOT(0)
    ADD_SPOT(1)
    ADD_SPOT(2)
    ADD_SPOT(3)
    ADD_SPOT(4)
    ADD_SPOT(5)
    ADD_SPOT(6)
    ADD_SPOT(7)

    float3 ambient = lighting.globalAmbient.xyz;
    float ambientMax = max(ambient.x, max(ambient.y, ambient.z));
    ambient = ambientMax <= 0.0001 ? float3(0.08) : ambient * max(1.0, 0.08 / ambientMax);
    ambient *= ambientVisibility(input.position.xy, albedo);
    // Linear HDR color. The pipeline owns display tone mapping and encoding.
    float3 shaded = color + ambient * albedo + albedo * surface.w;
#ifdef XRE_STANDARD_LIT_COVERAGE
    if (material.coverage.z > 0.5)
        shaded *= material.baseColorOpacity.w;
#endif
    return float4(shaded, material.baseColorOpacity.w);
}
