#include "SkyboxCommon.slang"

[[vk::binding(1, 1)]] TextureCube<float4> skyTexture;
[[vk::binding(2, 1)]] SamplerState skySampler;

[shader("fragment")]
float4 skyFragment(SkyVertexOutput input) : SV_Target0
{
    return float4(skyTexture.Sample(skySampler, normalize(input.worldDirection)).rgb * sky.intensity, 1.0);
}
