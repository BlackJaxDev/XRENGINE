#include "NativeVertexRasterInputs.slang"
#define standardLitVertex nativeVertexBaseline
#include "StandardLitColorLocalShadows.slang"
#undef standardLitVertex
[shader("vertex")]
LitVertexOutput standardLitVertex(LitVertexInput input)
{
    applyRasterNativeVertex(input.position, input.normal);
    return nativeVertexBaseline(input);
}
