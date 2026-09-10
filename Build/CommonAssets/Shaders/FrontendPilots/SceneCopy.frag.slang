// Authored GLSL counterpart: Scene3D/SceneCopy.fs. No position/depth rewrite.
[[vk::binding(0, 2)]] Sampler2D HDRSceneTex;

[shader("fragment")]
float4 copyScene([[vk::location(0)]] float3 FragPos : TEXCOORD0) : SV_Target0
{
    return HDRSceneTex.Sample(FragPos.xy * 0.5 + 0.5);
}
