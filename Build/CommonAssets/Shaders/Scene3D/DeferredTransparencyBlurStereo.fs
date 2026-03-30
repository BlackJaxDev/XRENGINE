#version 460
#extension GL_OVR_multiview2 : require

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray TransparentSceneCopyTex;
uniform sampler2DArray AlbedoOpacity;
uniform sampler2DArray DepthView;

const int BlurRadius = 2;
const float FarDepthThreshold = 0.99999;
const float NearOpaqueThreshold = 0.999;
const float DepthWeightScale = 96.0;

vec3 GetUv()
{
    return vec3(FragPos.xy * 0.5 + 0.5, gl_ViewID_OVR);
}

bool IsDeferredTranslucent(float opacity, float depth)
{
    return depth < FarDepthThreshold && opacity < NearOpaqueThreshold;
}

float SpatialWeight(ivec2 offset)
{
    return exp(-0.65 * float(dot(offset, offset)));
}

void main()
{
    vec3 uv = GetUv();
    vec4 centerScene = texture(TransparentSceneCopyTex, uv);
    float centerOpacity = clamp(texture(AlbedoOpacity, uv).a, 0.0, 1.0);
    float centerDepth = texture(DepthView, uv).r;
    bool centerTranslucent = IsDeferredTranslucent(centerOpacity, centerDepth);

    vec2 texelSize = 1.0 / vec2(textureSize(TransparentSceneCopyTex, 0));
    float referenceDepth = centerDepth;
    float nearestTranslucentDepth = 1.0;
    float neighborhoodTransparency = centerTranslucent ? 1.0 - centerOpacity : 0.0;

    for (int y = -BlurRadius; y <= BlurRadius; ++y)
    {
        for (int x = -BlurRadius; x <= BlurRadius; ++x)
        {
            vec3 sampleUv = vec3(uv.xy + vec2(x, y) * texelSize, uv.z);
            float sampleOpacity = clamp(texture(AlbedoOpacity, sampleUv).a, 0.0, 1.0);
            float sampleDepth = texture(DepthView, sampleUv).r;
            if (!IsDeferredTranslucent(sampleOpacity, sampleDepth))
                continue;

            nearestTranslucentDepth = min(nearestTranslucentDepth, sampleDepth);
            neighborhoodTransparency = max(neighborhoodTransparency, 1.0 - sampleOpacity);
        }
    }

    if (!centerTranslucent && nearestTranslucentDepth < 1.0)
        referenceDepth = nearestTranslucentDepth;

    if (neighborhoodTransparency <= 1e-4)
    {
        OutColor = centerScene;
        return;
    }

    vec3 filteredColor = vec3(0.0);
    float colorWeightSum = 0.0;
    float filteredOpacity = 0.0;
    float opacityWeightSum = 0.0;

    for (int y = -BlurRadius; y <= BlurRadius; ++y)
    {
        for (int x = -BlurRadius; x <= BlurRadius; ++x)
        {
            ivec2 offset = ivec2(x, y);
            vec3 sampleUv = vec3(uv.xy + vec2(offset) * texelSize, uv.z);
            vec4 sampleScene = texture(TransparentSceneCopyTex, sampleUv);
            float sampleOpacity = clamp(texture(AlbedoOpacity, sampleUv).a, 0.0, 1.0);
            float sampleDepth = texture(DepthView, sampleUv).r;
            float spatialWeight = SpatialWeight(offset);

            float depthWeight = 1.0;
            if (!(offset.x == 0 && offset.y == 0))
            {
                if (sampleDepth >= FarDepthThreshold || referenceDepth >= FarDepthThreshold)
                    depthWeight = 0.35;
                else
                    depthWeight = exp(-abs(sampleDepth - referenceDepth) * DepthWeightScale);
            }

            float weight = spatialWeight * depthWeight;
            filteredColor += sampleScene.rgb * weight;
            colorWeightSum += weight;

            if (IsDeferredTranslucent(sampleOpacity, sampleDepth))
            {
                float opacityWeight = weight * max(1.0 - sampleOpacity, 1e-3);
                filteredOpacity += sampleOpacity * opacityWeight;
                opacityWeightSum += opacityWeight;
            }
        }
    }

    vec3 blurColor = colorWeightSum > 0.0 ? filteredColor / colorWeightSum : centerScene.rgb;
    float blurOpacity = opacityWeightSum > 0.0 ? filteredOpacity / opacityWeightSum : centerOpacity;
    float blurStrength = clamp(neighborhoodTransparency * 1.5, 0.0, 1.0);
    if (!centerTranslucent)
        blurStrength *= 0.85;

    OutColor = vec4(
        mix(centerScene.rgb, blurColor, blurStrength),
        mix(centerScene.a, blurOpacity, blurStrength));
}