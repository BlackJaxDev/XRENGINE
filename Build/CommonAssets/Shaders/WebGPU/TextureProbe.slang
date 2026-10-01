#include "Depth.vert.slang"

[[vk::binding(0, 1)]] Texture2D<float4> materialTexture;
[[vk::binding(1, 1)]] SamplerState materialSampler;

struct TextureProbeInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float2 uv : TEXCOORD0;
};

struct TextureProbeOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

// Explicit diagnostic of the engine's canonical position/UV streams and sampler
// bindings. This is not a production material or a rendering fallback.
[shader("vertex")]
TextureProbeOutput textureProbeVertex(TextureProbeInput input)
{
    DepthVertexInput depthInput;
    depthInput.position = input.position;
    TextureProbeOutput output;
    output.position = depthVertex(depthInput);
    output.uv = input.uv;
    return output;
}

[shader("fragment")]
float4 textureProbeFragment(TextureProbeOutput input) : SV_Target0
{
    return materialTexture.Sample(materialSampler, input.uv);
}
