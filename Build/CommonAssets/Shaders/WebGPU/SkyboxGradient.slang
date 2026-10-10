#include "SkyboxCommon.slang"

// SolidColor publishes its top color into both endpoints through SkyboxComponent.
[shader("fragment")]
float4 skyFragment(SkyVertexOutput input) : SV_Target0
{
    float3 direction = normalize(input.worldDirection);
    float t = saturate(direction.y * 0.5 + 0.5);
    return float4(lerp(sky.bottomColor, sky.topColor, t) * sky.intensity, 1.0);
}
