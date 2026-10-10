#include "NativeVertexRasterInputs.slang"
#define spotShadowDepthVertex nativeShadowBaseline
#include "SpotShadowDepth.slang"
#undef spotShadowDepthVertex
struct NativeShadowVertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
};
[shader("vertex")]
float4 spotShadowDepthVertex(NativeShadowVertexInput input) : SV_Position
{
    applyRasterNativeVertex(input.position, input.normal);
    SpotDepthInput baseline;
    baseline.position = input.position;
    return nativeShadowBaseline(baseline);
}
