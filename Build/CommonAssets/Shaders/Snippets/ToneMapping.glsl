// Tone Mapping Utilities Snippet
// Matches tonemapping operators from ETonemappingType enum:
//   0 = Linear, 1 = Gamma, 2 = Clip, 3 = Reinhard, 4 = Hable,
//   5 = Mobius, 6 = ACES, 7 = Neutral, 8 = Filmic,
//   9 = AgX, 10 = GT7

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

const float XRENGINE_MOBIUS_DEFAULT_TRANSITION = 0.6;

// Linear - no tonemapping, just exposure
vec3 XRENGINE_LinearToneMap(vec3 hdr, float exposure)
{
    return hdr * exposure;
}

// Gamma only - applies gamma correction without compression
vec3 XRENGINE_GammaToneMap(vec3 hdr, float exposure, float gamma)
{
    return pow(max(hdr * exposure, vec3(0.0)), vec3(1.0 / max(gamma, 0.0001)));
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
vec3 XRENGINE_MobiusToneMap(vec3 hdr, float exposure, float transition)
{
    float a = max(transition, 0.0001);
    vec3 x = hdr * exposure;
    return (x * (a + 1.0)) / (x + a);
}

vec3 XRENGINE_MobiusToneMap(vec3 hdr, float exposure)
{
    return XRENGINE_MobiusToneMap(hdr, exposure, XRENGINE_MOBIUS_DEFAULT_TRANSITION);
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

// Filmic - classic ALU-friendly filmic response
vec3 XRENGINE_FilmicToneMap(vec3 hdr, float exposure)
{
    vec3 x = max(hdr * exposure - vec3(0.004), vec3(0.0));
    return (x * (6.2 * x + 0.5)) / (x * (6.2 * x + 1.7) + 0.06);
}

const mat3 XRENGINE_AGX_LINEAR_SRGB_TO_LINEAR_REC2020 = mat3(
     0.6274, 0.0691, 0.0164,
     0.3293, 0.9195, 0.0880,
     0.0433, 0.0113, 0.8956);

const mat3 XRENGINE_AGX_LINEAR_REC2020_TO_LINEAR_SRGB = mat3(
     1.6605, -0.1246, -0.0182,
    -0.5876,  1.1329, -0.1006,
    -0.0728, -0.0083,  1.1187);

const mat3 XRENGINE_AGX_INSET_MATRIX = mat3(
    0.856627153315983,  0.137318972929847, 0.111898212999950,
    0.095121240538159,  0.761241990602591, 0.076799418603190,
    0.048251606145858,  0.101439036467562, 0.811302368396859);

const mat3 XRENGINE_AGX_OUTSET_MATRIX = mat3(
     1.127100581814437, -0.141329763498438, -0.141329763498438,
    -0.110606643096603,  1.157823702216272, -0.110606643096603,
    -0.016493938717835, -0.016493938717834,  1.251936406595041);

const float XRENGINE_AGX_MIN_EV = -12.47393;
const float XRENGINE_AGX_MAX_EV = 4.026069;

vec3 XRENGINE_AgXDefaultContrastApprox(vec3 x)
{
    vec3 x2 = x * x;
    vec3 x4 = x2 * x2;
    return 15.5 * x4 * x2
        - 40.14 * x4 * x
        + 31.96 * x4
        - 6.868 * x2 * x
        + 0.4298 * x2
        + 0.1191 * x
        - 0.00232;
}

// AgX - Blender/Filament-style fitted display transform.
vec3 XRENGINE_AgXToneMap(vec3 hdr, float exposure)
{
    vec3 color = max(hdr * exposure, vec3(0.0));
    color = XRENGINE_AGX_LINEAR_SRGB_TO_LINEAR_REC2020 * color;
    color = XRENGINE_AGX_INSET_MATRIX * color;
    color = max(color, vec3(1.0e-10));
    color = clamp(log2(color), vec3(XRENGINE_AGX_MIN_EV), vec3(XRENGINE_AGX_MAX_EV));
    color = (color - vec3(XRENGINE_AGX_MIN_EV)) / (XRENGINE_AGX_MAX_EV - XRENGINE_AGX_MIN_EV);
    color = clamp(color, 0.0, 1.0);
    color = XRENGINE_AgXDefaultContrastApprox(color);
    color = XRENGINE_AGX_OUTSET_MATRIX * color;
    color = pow(max(color, vec3(0.0)), vec3(2.2));
    color = XRENGINE_AGX_LINEAR_REC2020_TO_LINEAR_SRGB * color;
    return clamp(color, 0.0, 1.0);
}

vec3 XRENGINE_GT7ToneMap(vec3 hdr, float exposure, float P, float a, float m, float l, float c, float b)
{
    vec3 x = max(hdr * exposure, vec3(0.0));
    float l0 = ((P - m) * l) / a;
    float S0 = m + l0;
    float S1 = m + a * l0;
    float C2 = (a * P) / max(P - S1, 1.0e-5);
    float CP = -C2 / P;

    vec3 w0 = vec3(1.0) - smoothstep(vec3(0.0), vec3(m), x);
    vec3 w2 = step(vec3(m + l0), x);
    vec3 w1 = vec3(1.0) - w0 - w2;

    vec3 T = vec3(m) * pow(max(x / vec3(m), vec3(0.0)), vec3(c)) + vec3(b);
    vec3 S = vec3(P) - (vec3(P - S1) * exp(vec3(CP) * (x - vec3(S0))));
    vec3 L = vec3(m) + vec3(a) * (x - vec3(m));

    return clamp(T * w0 + L * w1 + S * w2, vec3(0.0), vec3(P));
}

// GT7/GT - lightweight Uchimura curve: toe, linear midtones, and highlight shoulder.
vec3 XRENGINE_GT7ToneMap(vec3 hdr, float exposure)
{
    const float P = 1.0;  // max display brightness
    const float a = 1.0;  // contrast
    const float m = 0.22; // linear section start
    const float l = 0.4;  // linear section length
    const float c = 1.33; // toe strength
    const float b = 0.0;  // pedestal
    return XRENGINE_GT7ToneMap(hdr, exposure, P, a, m, l, c, b);
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
#define XRENGINE_TONEMAP_AGX      9
#define XRENGINE_TONEMAP_GT7      10

// Apply tonemapping by type index with exposure, gamma, and Mobius transition control
vec3 XRENGINE_ApplyToneMap(vec3 hdr, int tonemapType, float exposure, float gamma, float mobiusTransition)
{
    switch (tonemapType)
    {
        case XRENGINE_TONEMAP_LINEAR:   return XRENGINE_LinearToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_GAMMA:    return XRENGINE_GammaToneMap(hdr, exposure, gamma);
        case XRENGINE_TONEMAP_CLIP:     return XRENGINE_ClipToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_REINHARD: return XRENGINE_ReinhardToneMapExposed(hdr, exposure);
        case XRENGINE_TONEMAP_HABLE:    return XRENGINE_HableToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_MOBIUS:   return XRENGINE_MobiusToneMap(hdr, exposure, mobiusTransition);
        case XRENGINE_TONEMAP_ACES:     return XRENGINE_ACESToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_NEUTRAL:  return XRENGINE_NeutralToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_FILMIC:   return XRENGINE_FilmicToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_AGX:      return XRENGINE_AgXToneMap(hdr, exposure);
        case XRENGINE_TONEMAP_GT7:      return XRENGINE_GT7ToneMap(hdr, exposure);
        default:                        return XRENGINE_ReinhardToneMapExposed(hdr, exposure);
    }
}

vec3 XRENGINE_ApplyToneMap(vec3 hdr, int tonemapType, float exposure, float gamma)
{
    return XRENGINE_ApplyToneMap(hdr, tonemapType, exposure, gamma, XRENGINE_MOBIUS_DEFAULT_TRANSITION);
}

// Simplified version with default gamma of 2.2
vec3 XRENGINE_ApplyToneMapSimple(vec3 hdr, int tonemapType, float exposure)
{
    return XRENGINE_ApplyToneMap(hdr, tonemapType, exposure, 2.2, XRENGINE_MOBIUS_DEFAULT_TRANSITION);
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
