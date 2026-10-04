// Retain the canonical vertex computation and fragment entry unchanged.
#define standardLitVertex authoredOrderOriginalVertex
#include "StandardLitColorCoverage.slang"
#undef standardLitVertex
#include "AuthoredOrderGate.slang"

[shader("vertex")]
LitVertexOutput standardLitVertex(LitVertexInput input)
{
    LitVertexOutput output = authoredOrderOriginalVertex(input);
    output.position = authoredOrderPosition(output.position);
    return output;
}
