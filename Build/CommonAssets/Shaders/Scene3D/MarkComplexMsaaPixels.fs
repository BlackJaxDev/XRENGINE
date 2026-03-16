#version 460

// Complex pixel detection for MSAA deferred rendering.
// Compares MSAA samples in the GBuffer; if normal or depth differ significantly
// across any pair of samples, the pixel is "complex" (on a geometric edge).
//
// This shader is rendered as a fullscreen pass. It outputs nothing to color —
// GL stencil operations in the material's RenderingParameters write a stencil
// bit so later lighting passes can branch per-pixel vs per-sample.

layout(location = 0) in vec3 FragPos;

layout(binding = 0) uniform sampler2DMS NormalMS;
layout(binding = 1) uniform sampler2DMS DepthMS;
uniform int         SampleCount;
uniform float       NormalThreshold; // dot threshold (e.g., 0.99)
uniform float       DepthThreshold;  // abs difference (e.g., 0.001)

void main()
{
    ivec2 coord = ivec2(gl_FragCoord.xy);

    // Sample 0 as reference.
    vec2 n0 = texelFetch(NormalMS, coord, 0).rg;
    float d0 = texelFetch(DepthMS, coord, 0).r;

    for (int i = 1; i < SampleCount; ++i)
    {
        vec2 ni = texelFetch(NormalMS, coord, i).rg;
        float di = texelFetch(DepthMS, coord, i).r;

        // Normal divergence: encoded normals are in RG16F, compare via dot-like metric.
        float normalDiff = dot(n0 - ni, n0 - ni);
        float depthDiff  = abs(d0 - di);

        if (normalDiff > (1.0 - NormalThreshold) || depthDiff > DepthThreshold)
        {
            // Complex pixel — the stencil write (configured on the material)
            // will mark this pixel. We just need the fragment to survive.
            return;
        }
    }

    // Simple pixel — discard so stencil is NOT written.
    discard;
}
