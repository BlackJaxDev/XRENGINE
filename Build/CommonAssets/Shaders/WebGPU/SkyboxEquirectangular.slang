#include "SkyboxCommon.slang"

[[vk::binding(1, 1)]] Texture2D<float4> skyTexture;
[[vk::binding(2, 1)]] SamplerState skySampler;

[shader("fragment")]
float4 skyFragment(SkyVertexOutput input) : SV_Target0
{
    float3 direction = normalize(input.worldDirection);
    float phi = atan2(direction.z, direction.x);
    float theta = asin(clamp(direction.y, -1.0, 1.0));
    float2 uv = float2(phi / 6.28318530718 + 0.5, 1.0 - (theta / 3.14159265359 + 0.5));
    return float4(skyTexture.Sample(skySampler, uv).rgb * sky.intensity, 1.0);
}
