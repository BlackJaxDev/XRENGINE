// =====================================================
// DitheredTransparency snippet
// =====================================================
// Provides a 4x4 ordered Bayer dither pattern for approximating
// transparency in deferred and other single-pass pipelines where
// alpha blending is not available.
//
// Usage:
//   #pragma snippet "DitheredTransparency"
//   ...
//   XRENGINE_DitheredAlphaDiscard(opacity, gl_FragCoord.xy);

// 4x4 Bayer ordered-dither threshold matrix (values in [0, 1]).
float XRENGINE_BayerDither4x4(vec2 fragCoord)
{
    int x = int(fragCoord.x) & 3;
    int y = int(fragCoord.y) & 3;

    // Classic 4x4 Bayer matrix, normalized to [0..1) range.
    const float bayer[16] = float[16](
         0.0 / 16.0,  8.0 / 16.0,  2.0 / 16.0, 10.0 / 16.0,
        12.0 / 16.0,  4.0 / 16.0, 14.0 / 16.0,  6.0 / 16.0,
         3.0 / 16.0, 11.0 / 16.0,  1.0 / 16.0,  9.0 / 16.0,
        15.0 / 16.0,  7.0 / 16.0, 13.0 / 16.0,  5.0 / 16.0
    );

    return bayer[y * 4 + x];
}

// Discards the fragment when opacity is below the dither threshold.
// opacity: value in [0, 1], where 0 = fully transparent and 1 = fully opaque.
// fragCoord: typically gl_FragCoord.xy.
void XRENGINE_DitheredAlphaDiscard(float opacity, vec2 fragCoord)
{
    if (opacity < 1.0)
    {
        float threshold = XRENGINE_BayerDither4x4(fragCoord);
        if (opacity <= threshold)
            discard;
    }
}

// Combined alpha-cutoff and dithered transparency test.
// First applies hard alpha cutoff (discard if texAlpha < alphaCutoff),
// then applies dithered transparency using opacity.
// alphaCutoff: hard cutoff threshold, use negative value to skip.
// texAlpha: alpha channel from the texture sample.
// opacity: overall opacity for dithering (uniform), use 1.0 to skip dithering.
// fragCoord: typically gl_FragCoord.xy.
void XRENGINE_AlphaCutoffAndDither(float alphaCutoff, float texAlpha, float opacity, vec2 fragCoord)
{
    // Hard alpha cutoff (masked transparency).
    if (alphaCutoff >= 0.0 && texAlpha < alphaCutoff)
        discard;

    // Dithered transparency approximation.
    XRENGINE_DitheredAlphaDiscard(opacity, fragCoord);
}
