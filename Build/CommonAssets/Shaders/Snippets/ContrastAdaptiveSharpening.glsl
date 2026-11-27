// Robust Contrast Adaptive Sharpening (RCAS) for post-resolve temporal anti-aliasing.
// Runs outside the temporal history feedback loop on display-referred color so it
// restores crisp detail without compounding halos or re-injecting jitter shimmer.

float XRENGINE_RcasLuma(vec3 rgb)
{
    return dot(rgb, vec3(0.25, 0.5, 0.25));
}

// Applies a 5-tap contrast-adaptive sharpening filter to center sample e and its
// four axial neighbors (b = up, d = left, f = right, h = down).
// sharpness is in [0.0, 1.0]; 0.0 returns e unchanged.
vec3 XRENGINE_EvaluateRcas(vec3 e, vec3 b, vec3 d, vec3 f, vec3 h, float sharpness)
{
    float clampedSharpness = clamp(sharpness, 0.0, 1.0);
    if (clampedSharpness <= 1e-5)
        return e;

    vec3 ce = max(e, vec3(0.0));
    vec3 cb = max(b, vec3(0.0));
    vec3 cd = max(d, vec3(0.0));
    vec3 cf = max(f, vec3(0.0));
    vec3 ch = max(h, vec3(0.0));

    // Bound negative filter lobes per channel so sharpened values stay within [0, peak]
    // for both SDR [0, 1] and HDR (> 1) targets without ringing high-contrast silhouettes.
    vec3 mn4 = min(min(cb, cd), min(cf, ch));
    vec3 mx4 = max(max(cb, cd), max(cf, ch));
    vec3 peak = max(vec3(1.0), max(mx4, ce));

    vec3 lowerLimit = -mn4 / max(4.0 * mx4, vec3(1e-5));
    vec3 upperLimit = -(peak - mx4) / max(4.0 * (peak - mn4), vec3(1e-5));
    vec3 lobeRgb = max(lowerLimit, upperLimit);
    float lobe = max(max(lobeRgb.r, lobeRgb.g), lobeRgb.b);

    // Attenuate sharpening on isolated single-pixel luma impulses to suppress noise.
    float eL = XRENGINE_RcasLuma(ce);
    float bL = XRENGINE_RcasLuma(cb);
    float dL = XRENGINE_RcasLuma(cd);
    float fL = XRENGINE_RcasLuma(cf);
    float hL = XRENGINE_RcasLuma(ch);
    float minL = min(eL, min(min(bL, dL), min(fL, hL)));
    float maxL = max(eL, max(max(bL, dL), max(fL, hL)));
    float nz = 0.25 * (bL + dL + fL + hL) - eL;
    float noiseAtten = 1.0 - 0.5 * clamp(abs(nz) / max(maxL - minL, 1e-5), 0.0, 1.0);

    // Cap negative lobe magnitude at 0.1875 (3/16) so denominator (4 * w + 1) >= 0.25.
    float weight = clamp(lobe, -0.1875, 0.0) * clampedSharpness * noiseAtten;
    vec3 sharpened = (weight * (cb + cd + cf + ch) + ce) / (4.0 * weight + 1.0);
    return max(sharpened, vec3(0.0));
}
