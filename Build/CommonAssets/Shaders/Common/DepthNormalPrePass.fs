#version 450

#pragma snippet "NormalEncoding"

// Forward depth+normal pre-pass override shader.
// Writes world-space normals (matching GBuffer Normal format) to color attachment 0
// and the render identity to color attachment 1, while depth is written implicitly
// to the depth/stencil attachment.

layout(location = 0) out vec2 Normal;

layout(location = 1) out uint TransformId;

layout(location = 1) in vec3 FragNorm;
layout(location = 27) flat in uint FragRenderIdentityId;

void main()
{
    Normal = XRENGINE_EncodeNormal(FragNorm);
    TransformId = FragRenderIdentityId;
}
