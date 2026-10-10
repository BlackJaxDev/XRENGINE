#include "SkyboxCommon.slang"

[[vk::binding(1, 1)]] Texture2D<float4> skyTexture;
[[vk::binding(2, 1)]] SamplerState skySampler;

[shader("fragment")]
float4 skyFragment(SkyVertexOutput input) : SV_Target0
{
    float3 direction = normalize(input.worldDirection);
    // Preserve the desktop convention: world Y is octahedral Z.
    float3 octDirection = direction.xzy;
    octDirection /= max(abs(octDirection.x) + abs(octDirection.y) + abs(octDirection.z), 1e-5);
    float2 uv = octDirection.xy;
    if (octDirection.z < 0.0)
    {
        float2 signs = float2(octDirection.x >= 0.0 ? 1.0 : -1.0, octDirection.y >= 0.0 ? 1.0 : -1.0);
        uv = (1.0 - abs(uv.yx)) * signs;
    }
    return float4(skyTexture.Sample(skySampler, uv * 0.5 + 0.5).rgb * sky.intensity, 1.0);
}
