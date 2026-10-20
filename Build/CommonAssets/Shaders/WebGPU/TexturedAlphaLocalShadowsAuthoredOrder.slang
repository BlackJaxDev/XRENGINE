// Preserve the exact vertex calculation and fragment entry behind final-position rank gating.
#define standardLitVertex authoredOrderOriginalVertex
#include "TexturedAlphaLocalShadows.slang"
#undef standardLitVertex
#include "AuthoredOrderGate.slang"
[shader("vertex")]
LitVertexOutput standardLitVertex(LitVertexInput input)
{
    LitVertexOutput output = authoredOrderOriginalVertex(input);
    output.position = authoredOrderPosition(output.position);
    return output;
}
