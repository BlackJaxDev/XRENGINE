#include "Depth.vert.slang"

[[vk::binding(0, 1)]] Texture2D<float4> materialTexture;
[[vk::binding(1, 1)]] SamplerState materialSampler;

struct CanvasSurfaceInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float2 uv : TEXCOORD0;
};
struct CanvasSurfaceOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[shader("vertex")]
CanvasSurfaceOutput canvasSurfaceVertex(CanvasSurfaceInput input)
{
    DepthVertexInput depthInput;
    depthInput.position = input.position;
    CanvasSurfaceOutput output;
    output.position = depthVertex(depthInput);
    // The shared quad has bottom-left UV zero; a WebGPU rendered texture has
    // top-first rows. Convert once here, leaving desktop sampling unchanged.
    output.uv = float2(input.uv.x, 1.0 - input.uv.y);
    return output;
}

[shader("fragment")]
float4 canvasSurfaceFragment(CanvasSurfaceOutput input) : SV_Target0
{
    // The owned canvas target already contains linear premultiplied RGBA.
    return materialTexture.Sample(materialSampler, input.uv);
}
