#define impostorVertex authoredOrderOriginalVertex
#include "OctahedralImpostor.slang"
#undef impostorVertex
#include "AuthoredOrderGate.slang"

[shader("vertex")]
ImpostorVertexOutput impostorVertex(ImpostorVertexInput input)
{
    ImpostorVertexOutput output = authoredOrderOriginalVertex(input);
    output.position = authoredOrderPosition(output.position);
    return output;
}
