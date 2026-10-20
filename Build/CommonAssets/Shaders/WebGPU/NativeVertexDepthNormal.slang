#include "NativeVertexRasterInputs.slang"
#define depthNormalVertex nativeDepthNormalBaseline
#include "DepthNormalPrePass.slang"
#undef depthNormalVertex
[shader("vertex")]
DepthNormalOutput depthNormalVertex(DepthNormalInput input)
{
    applyRasterNativeVertex(input.position, input.normal);
    return nativeDepthNormalBaseline(input);
}
