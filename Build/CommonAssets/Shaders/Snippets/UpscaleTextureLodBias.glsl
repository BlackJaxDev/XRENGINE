#ifndef XRENGINE_UPSCALE_TEXTURE_LOD_BIAS_SNIPPET
#define XRENGINE_UPSCALE_TEXTURE_LOD_BIAS_SNIPPET

// Automatic negative texture LOD bias (log2(renderScale)) applied during
// reduced-resolution temporal upscaling (TSR, DLSS, XeSS). Zero at native size.
uniform float UpscaleTextureLodBias = 0.0f;

vec4 XRENGINE_SampleBiasedTexture(sampler2D surfaceTexture, vec2 uv)
{
    return texture(surfaceTexture, uv, UpscaleTextureLodBias);
}

#endif
