// Tone Mapping Utilities Snippet
// Matches tonemapping operators from ETonemappingType enum:
//   0 = Linear, 1 = Gamma, 2 = Clip, 3 = Reinhard, 4 = Hable,
//   5 = Mobius, 6 = ACES, 7 = Neutral, 8 = Filmic

// ============================================================================
// Exposure Helpers
// ============================================================================

vec3 XRENGINE_ApplyExposure(vec3 color, float exposure)
{
    return color * pow(2.0, exposure);
}

vec3 XRENGINE_ApplyExposureLinear(vec3 color, float exposureMultiplier)
{
    return color * exposureMultiplier;
}

// ============================================================================
// Individual Tonemapping Operators
// ============================================================================

// Linear - no tonemapping, just exposure
vec3 XRENGINE_LinearToneMap(vec3 hdr, float exposure)
{
    return hdr * exposure;
}

// Gamma only - applies gamma correction without compression
vec3 XRENGINE_GammaToneMap(vec3 hdr, float exposure, float gamma)
{
    return pow(hdr * exposure, vec3(1.0 / gamma));
}

// Clip - simple clamp after exposure
vec3 XRENGINE_ClipToneMap(vec3 hdr, float exposure)
{
    return clamp(hdr * exposure, 0.0, 1.0);
}

// Reinhard - simple luminance-based compression
vec3 XRENGINE_ReinhardToneMap(vec3 hdr)
{
    return hdr / (hdr + vec3(1.0));
}

vec3 XRENGINE_ReinhardToneMapExposed(vec3 hdr, float exposure)
{
    vec3 x = hdr * exposure;
    return x / (x + vec3(1.0));
}

// Reinhard Extended - with white point control
vec3 XRENGINE_ReinhardExtendedToneMap(vec3 hdr, float whitePoint)
{
    vec3 numerator = hdr * (1.0 + hdr / (whitePoint * whitePoint));
    return numerator / (1.0 + hdr);
}

// Hable/Uncharted 2 - filmic curve
vec3 XRENGINE_HableToneMap(vec3 hdr, float exposure)
{
    const float A = 0.15, B = 0.50, C = 0.10, D = 0.20, E = 0.02, F = 0.30;
    vec3 x = max(hdr * exposure - E, vec3(0.0));
    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}

// Mobius - simple and fast with good highlight rolloff
vec3 XRENGINE_MobiusToneMap(vec3 hdr, float exposure)
{
    float a = 0.6;
    vec3 x = hdr * exposure;
    return (x * (a + 1.0)) / (x + a);
}

// ACES Filmic - industry standard cinematic look
vec3 XRENGINE_ACESToneMap(vec3 hdr, float exposure)
{
    vec3 x = hdr * exposure;
    return (x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14);
}

// Simplified ACES without exposure parameter
vec3 XRENGINE_ACESFilmicToneMap(vec3 x)
{
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

// Neutral - balanced filmic curve
vec3 XRENGINE_NeutralToneMap(vec3 hdr, float exposure)
{
    vec3 x = hdr * exposure;
    return (x * (x + 0.0245786)) / (x * (0.983729 * x + 0.432951) + 0.238081);
}

// Filmic - alternative filmic response (same formula as Neutral in PostProcess.fs)
vec3 XRENGINE_FilmicToneMap(vec3 hdr, float exposure)
{
    vec3 x = hdr * exposure;
    return (x * (x + 0.0245786)) / (x * (0.983729 * x + 0.432951) + 0.238081);
}

// ============================================================================
// Unified Tonemapping by Type Index
// Matches ETonemappingType enum values
// ============================================================================

#define XRENGINE_TONEMAP_LINEAR   0
#define XRENGINE_TONEMAP_GAMMA    1
#define XRENGINE_TONEMAP_CLIP     2
#define XRENGINE_TONEMAP_REINHARD 3
#define XRENGINE_TONEMAP_HABLE    4
#define XRENGINE_TONEMAP_MOBIUS   5
#define XRENGINE_TONEMAP_ACES     6
#define XRENGINE_TONEMAP_NEUTRAL  7
#define XRENGINE_TONEMAP_FILMIC   8

// Apply tonemapping by type index with exposure and gamma
vec3 XRENGINE_ApplyToneMap(vec3 hdr, int tonemapType, float exposure, float gamma)
{
    switch (tonemapType)
    {
        case XRENGINE_TONEMAP_LINEAR:   return XRENGINE_LinearToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_GAMMA:    return XRENGINE_GammaToneMap(hdr, exposure, gamma);
        case XRENGINE_TONEMAP_CLIP:     return XRENGINE_ClipToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_REINHARD: return XRENGINE_ReinhardToneMapExposed(hdr, exposure);
        case XRENGINE_TONEMAP_HABLE:    return XRENGINE_HableToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_MOBIUS:   return XRENGINE_MobiusToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_ACES:     return XRENGINE_ACESToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_NEUTRAL:  return XRENGINE_NeutralToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_FILMIC:   return XRENGINE_FilmicToneMap(hdr, exposure);
        default:                        return XRENGINE_ReinhardToneMapExposed(hdr, exposure);
    }
}

// Simplified version with default gamma of 2.2
vec3 XRENGINE_ApplyToneMapSimple(vec3 hdr, int tonemapType, float exposure)
{
    return XRENGINE_ApplyToneMap(hdr, tonemapType, exposure, 2.2);
}

// ============================================================================
// Gamma Correction Helpers
// ============================================================================

vec3 XRENGINE_LinearToGamma(vec3 linear, float gamma)
{
    return pow(linear, vec3(1.0 / gamma));
}

vec3 XRENGINE_GammaToLinear(vec3 gamma_color, float gamma)
{
    return pow(gamma_color, vec3(gamma));
}

// sRGB approximation (gamma 2.2)
vec3 XRENGINE_LinearToSRGBFast(vec3 linear)
{
    return pow(linear, vec3(1.0 / 2.2));
}

vec3 XRENGINE_SRGBToLinearFast(vec3 srgb)
{
    return pow(srgb, vec3(2.2));
}

// Precise sRGB conversion with linear segment
vec3 XRENGINE_LinearToSRGBPrecise(vec3 linear)
{
    vec3 higher = pow(linear, vec3(1.0 / 2.4)) * 1.055 - 0.055;
    vec3 lower = linear * 12.92;
    return mix(lower, higher, step(vec3(0.0031308), linear));
}

vec3 XRENGINE_SRGBToLinearPrecise(vec3 srgb)
{
    vec3 higher = pow((srgb + 0.055) / 1.055, vec3(2.4));
    vec3 lower = srgb / 12.92;
    return mix(lower, higher, step(vec3(0.04045), srgb));
}
