#define XRE_AUTHORED_TEXTURED_ORDER_GATE 1
#include "AuthoredTexturedLocalShadows.slang"
#include "AuthoredOrderGate.slang"
[shader("vertex")]
AuthoredTexturedVertexOutput standardLitVertex(AuthoredTexturedVertexInput input)
{
    AuthoredTexturedVertexOutput output = authoredTexturedForwardVertex(input);
    output.position = authoredOrderPosition(output.position);
    return output;
}
