#version 450

#pragma snippet "NormalEncoding"

// Forward depth+normal pre-pass override shader.
// Writes world-space normals (matching GBuffer Normal format) to color attachment 0
// while depth is written implicitly to the depth/stencil attachment.

layout(location = 0) out vec2 Normal;

layout(location = 1) in vec3 FragNorm;

void main()
{
    Normal = XRENGINE_EncodeNormal(FragNorm);
}
