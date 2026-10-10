#include "NativeVertexRasterInputs.slang"
#define pointShadowDepthVertex nativeShadowBaseline
#include "PointShadowDepth.slang"
#undef pointShadowDepthVertex
struct NativeShadowVertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
};
[shader("vertex")]
PointDepthOutput pointShadowDepthVertex(NativeShadowVertexInput input)
{
    applyRasterNativeVertex(input.position, input.normal);
    PointDepthInput baseline;
    baseline.position = input.position;
    return nativeShadowBaseline(baseline);
}
