// StandardLitColorV1 browser raster lowering. Direct lighting and the no-probe
// ambient term match Snippets/ForwardLighting.glsl and LightAttenuation.glsl.
// This bounded variant admits 4 directional, 8 point, and 8 spot lights. Its host
// must reject overflow and unsupported shadow/probe/GI requirements explicitly.
// Opaque IndexOfRefraction has no lighting effect, matching the desktop path.

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
};

[[vk::binding(0, 0)]] ConstantBuffer<LitViewUniforms> view;
[[vk::binding(1, 0)]] ConstantBuffer<LitObjectUniforms> object;
[[vk::binding(0, 1)]] ConstantBuffer<LitMaterialUniforms> material;
[[vk::binding(0, 2)]] ConstantBuffer<LitLightingUniforms> lighting;

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

float3 directionalLight(float4 direction, float4 colorIntensity,
    float3 normal, float3 viewDirection, float3 albedo, float3 rms)
{
    return directPbr(colorIntensity.xyz, colorIntensity.w, safeNormalize(-direction.xyz),
        normal, viewDirection, albedo, rms, 1.0);
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

#define ADD_DIRECTIONAL(N) if (lighting.lightCounts.x > N) color += directionalLight(lighting.directional##N##Direction, lighting.directional##N##ColorIntensity, normal, viewDirection, albedo, rms);
#define ADD_POINT(N) if (lighting.lightCounts.y > N) color += pointLight(lighting.point##N##PositionRadius, lighting.point##N##ColorIntensity, lighting.point##N##Brightness, input.worldPosition, normal, viewDirection, albedo, rms);
#define ADD_SPOT(N) if (lighting.lightCounts.z > N) color += spotLight(lighting.spot##N##PositionRadius, lighting.spot##N##ColorIntensity, lighting.spot##N##DirectionExponent, lighting.spot##N##CutoffsBrightness, input.worldPosition, normal, viewDirection, albedo, rms);

[shader("fragment")]
float4 standardLitFragment(LitVertexOutput input) : SV_Target0
{
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
    // Linear HDR color. The pipeline owns display tone mapping and encoding.
    return float4(color + ambient * albedo + albedo * surface.w, material.baseColorOpacity.w);
}
