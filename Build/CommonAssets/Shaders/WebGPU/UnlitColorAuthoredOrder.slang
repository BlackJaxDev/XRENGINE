#define UNLIT_KIND 1
#define UNLIT_PASS 1
#define unlitVertex unlitOriginalVertex
#include "UnlitSurface.slang"
#undef unlitVertex
#include "AuthoredOrderGate.slang"
[shader("vertex")]
UnlitVertexOutput unlitVertex(UnlitVertexInput input)
{
    UnlitVertexOutput output = unlitOriginalVertex(input);
    output.position = authoredOrderPosition(output.position);
    return output;
}
