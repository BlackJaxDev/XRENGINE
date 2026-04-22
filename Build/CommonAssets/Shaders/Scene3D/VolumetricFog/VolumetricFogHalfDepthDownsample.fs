#version 450

// Single-tap half-resolution depth downsample for the volumetric fog chain.
//
// Writes raw (un-resolved) depth so the half-res scatter can use the same
// XRENGINE_ResolveDepth() / XRENGINE_WorldPosFromDepthRaw() helpers as the
// full-res path. The bilateral upscale weights half-res taps against the
// full-res depth view, so this half-res depth serves only as the reference
// depth of the raymarch sample at each half-res pixel.
//
// Downsample strategy: single tap at the half-res pixel's UV. The destination
// FBO is sized at half of the full-res viewport, so the same [0,1] UV space
// maps cleanly; bilinear filtering is intentionally avoided (nearest) to
// keep per-pixel depth crisp for the upscale's edge weighting.

layout(location = 0) out float OutDepth;
layout(location = 0) in vec3 FragPos;

uniform sampler2D DepthView;

void main()
{
    vec2 ndc = FragPos.xy;
    if (ndc.x > 1.0f || ndc.y > 1.0f)
        discard;
    vec2 uv = ndc * 0.5f + 0.5f;
    OutDepth = texture(DepthView, uv).r;
}
