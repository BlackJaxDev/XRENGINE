#version 460
#extension GL_OVR_multiview2 : require
// Depth-aware upsample of the blurred half-resolution GTAO to the full-resolution AO texture.
// Each output pixel blends its four nearest reduced texels with bilinear weights, scaled by how
// close the depth of each reduced texel is to the depth of this pixel. A reduced texel on another
// surface gets almost no weight, so AO does not bleed across depth edges. When no reduced texel
// is on the same surface, the pixel takes the texel with the closest depth.
#include "AOCommon.glsl"
#include "GTAOHalfDepthCommon.glsl"

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray GTAOInputTexture;
uniform sampler2DArray DepthView;

uniform mat4 LeftEyeInverseProjMatrix;
uniform mat4 RightEyeInverseProjMatrix;

// Relative view-depth difference that lowers a texel weight to 1/e.
const float DepthTolerance = 0.025f;

void main()
{
    if (FragPos.x > 1.0f || FragPos.y > 1.0f)
        discard;
    vec2 uv = AOTextureUVFromFragPos(FragPos);
    float depth = texture(DepthView, vec3(uv, gl_ViewID_OVR)).r;
    if (AOIsFarDepth(depth))
    {
        OutIntensity = 1.0f;
        return;
    }

    mat4 inverseProjMatrix = gl_ViewID_OVR == 0 ? LeftEyeInverseProjMatrix : RightEyeInverseProjMatrix;
    float viewDepth = max(abs(AOViewPosFromDepth(depth, uv, inverseProjMatrix).z), 1e-4f);
    ivec2 fullSize = textureSize(DepthView, 0).xy;
    ivec2 halfSize = textureSize(GTAOInputTexture, 0).xy;
    vec2 halfPosition = uv * vec2(halfSize) - 0.5f;
    ivec2 baseTexel = ivec2(floor(halfPosition));
    vec2 fraction = halfPosition - vec2(baseTexel);

    float result = 0.0f;
    float weightSum = 0.0f;
    float bestDifference = 3.4e38f;
    float bestAo = 1.0f;
    for (int i = 0; i < 4; ++i)
    {
        ivec2 offset = ivec2(i & 1, i >> 1);
        ivec2 halfTexel = clamp(baseTexel + offset, ivec2(0), halfSize - 1);
        vec4 block = textureGather(DepthView, vec3(XR_GTAO_HalfBlockGatherUV(halfTexel, fullSize), gl_ViewID_OVR), 0);
        float texelDepth = block[XR_GTAO_HalfBlockPick(block, halfTexel)];
        float ao = texelFetch(GTAOInputTexture, ivec3(halfTexel, gl_ViewID_OVR), 0).r;
        float difference = AOIsFarDepth(texelDepth)
            ? 3.0e38f
            : abs(abs(AOViewPosFromDepth(texelDepth, uv, inverseProjMatrix).z) - viewDepth) / viewDepth;
        if (difference < bestDifference)
        {
            bestDifference = difference;
            bestAo = ao;
        }
        vec2 bilinear = mix(vec2(1.0f) - fraction, fraction, vec2(offset));
        float weight = bilinear.x * bilinear.y * exp(-min(difference, 100.0f) / DepthTolerance);
        result += ao * weight;
        weightSum += weight;
    }

    OutIntensity = weightSum > 1e-4f ? clamp(result / weightSum, 0.0f, 1.0f) : bestAo;
}
