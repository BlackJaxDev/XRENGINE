#include "NativeVertexRasterInputs.slang"
#define depthVertex nativeShadowBaseline
#include "Depth.vert.slang"
#undef depthVertex
struct NativeShadowVertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
};
[shader("vertex")]
float4 depthVertex(NativeShadowVertexInput input) : SV_Position
{
    applyRasterNativeVertex(input.position, input.normal);
    DepthVertexInput baseline;
    baseline.position = input.position;
    return nativeShadowBaseline(baseline);
}
