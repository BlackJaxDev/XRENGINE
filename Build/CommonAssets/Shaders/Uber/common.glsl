// Uber Shader - Common Definitions

#ifndef TOON_COMMON_GLSL
#define TOON_COMMON_GLSL

// Constants
#define PI 3.14159265359
#define TWO_PI 6.28318530718
#define PI_OVER_2 1.5707963
#define PI_OVER_4 0.785398
#define EPSILON 0.000001

// Rendering modes
#define BLEND_MODE_OPAQUE 0
#define BLEND_MODE_CUTOUT 1
#define BLEND_MODE_FADE 2
#define BLEND_MODE_TRANSPARENT 3
#define BLEND_MODE_ADDITIVE 4

// Blend types for effects
#define BLEND_REPLACE 0
#define BLEND_DARKEN 1
#define BLEND_MULTIPLY 2
#define BLEND_LIGHTEN 5
#define BLEND_SCREEN 6
#define BLEND_SUBTRACT 7
#define BLEND_ADD 8
#define BLEND_OVERLAY 9
#define BLEND_MIXED 20

// UV modes
#define UV_MODE_UV0 0
#define UV_MODE_UV1 1
#define UV_MODE_UV2 2
#define UV_MODE_UV3 3
#define UV_MODE_PANOSPHERE 4
#define UV_MODE_WORLD_POS 5
#define UV_MODE_POLAR 6
#define UV_MODE_DISTORTED 7
#define UV_MODE_LOCAL_POS 8

// ============================================
// Utility Functions
// ============================================

// HLSL saturate equivalent
float saturate(float x) {
    return clamp(x, 0.0, 1.0);
}

vec2 saturate(vec2 x) {
    return clamp(x, 0.0, 1.0);
}

vec3 saturate(vec3 x) {
    return clamp(x, 0.0, 1.0);
}

vec4 saturate(vec4 x) {
    return clamp(x, 0.0, 1.0);
}

// Remap value from one range to another
float remap(float value, float low1, float high1, float low2, float high2) {
    return low2 + (value - low1) * (high2 - low2) / (high1 - low1);
}

// Inverse lerp
float inverseLerp(float a, float b, float value) {
    return (value - a) / (b - a);
}

// GLSL mod that matches HLSL behavior better for negative numbers
float glsl_mod(float x, float y) {
    return x - y * floor(x / y);
}

vec2 glsl_mod(vec2 x, vec2 y) {
    return x - y * floor(x / y);
}

// Luminance calculation
float luminance(vec3 color) {
    return dot(color, vec3(0.299, 0.587, 0.114));
}

// sRGB to Linear conversion
vec3 sRGBToLinear(vec3 color) {
    return pow(color, vec3(2.2));
}

vec4 sRGBToLinear(vec4 color) {
    return vec4(pow(color.rgb, vec3(2.2)), color.a);
}

// Linear to sRGB conversion
vec3 linearToSRGB(vec3 color) {
    return pow(color, vec3(1.0 / 2.2));
}

vec4 linearToSRGB(vec4 color) {
    return vec4(pow(color.rgb, vec3(1.0 / 2.2)), color.a);
}

// ============================================
// UV Utilities
// ============================================

vec2 transformUV(vec2 uv, vec4 texST) {
    return uv * texST.xy + texST.zw;
}

vec2 panUV(vec2 uv, vec2 pan, float time) {
    return uv + pan * time;
}

// Rotate UV around center
vec2 rotateUV(vec2 uv, float angle, vec2 center) {
    float s = sin(angle);
    float c = cos(angle);
    uv -= center;
    vec2 rotated = vec2(
        uv.x * c - uv.y * s,
        uv.x * s + uv.y * c
    );
    return rotated + center;
}

// Polar UV conversion
vec2 polarUV(vec3 direction) {
    float theta = atan(direction.z, direction.x);
    float phi = acos(direction.y);
    return vec2(theta / TWO_PI + 0.5, phi / PI);
}

// Panosphere UV
vec2 panosphereUV(vec3 viewDir, vec3 normal) {
    vec3 reflected = reflect(-viewDir, normal);
    return polarUV(reflected);
}

// ============================================
// Color Utilities
// ============================================

// Blend modes
vec3 blendOverlay(vec3 base, vec3 blend) {
    return mix(
        2.0 * base * blend,
        1.0 - 2.0 * (1.0 - base) * (1.0 - blend),
        step(0.5, base)
    );
}

vec3 blendScreen(vec3 base, vec3 blend) {
    return 1.0 - (1.0 - base) * (1.0 - blend);
}

vec3 blendSoftLight(vec3 base, vec3 blend) {
    return mix(
        2.0 * base * blend + base * base * (1.0 - 2.0 * blend),
        sqrt(base) * (2.0 * blend - 1.0) + 2.0 * base * (1.0 - blend),
        step(0.5, blend)
    );
}

vec4 blendColors(vec4 base, vec4 blend, int blendMode, float alpha) {
    vec3 result;
    
    switch(blendMode) {
        case BLEND_REPLACE:
            result = blend.rgb;
            break;
        case BLEND_MULTIPLY:
            result = base.rgb * blend.rgb;
            break;
        case BLEND_ADD:
            result = base.rgb + blend.rgb;
            break;
        case BLEND_SUBTRACT:
            result = base.rgb - blend.rgb;
            break;
        case BLEND_SCREEN:
            result = blendScreen(base.rgb, blend.rgb);
            break;
        case BLEND_OVERLAY:
            result = blendOverlay(base.rgb, blend.rgb);
            break;
        case BLEND_DARKEN:
            result = min(base.rgb, blend.rgb);
            break;
        case BLEND_LIGHTEN:
            result = max(base.rgb, blend.rgb);
            break;
        case BLEND_MIXED:
            result = mix(base.rgb * blend.rgb, blendScreen(base.rgb, blend.rgb), blend.rgb);
            break;
        default:
            result = blend.rgb;
            break;
    }
    
    return vec4(mix(base.rgb, result, alpha * blend.a), base.a);
}

// ============================================
// HSV / Hue Shift
// ============================================

vec3 rgbToHsv(vec3 c) {
    vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
    vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));
    
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

vec3 hsvToRgb(vec3 c) {
    vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

vec3 hueShift(vec3 color, float shift) {
    vec3 hsv = rgbToHsv(color);
    hsv.x = fract(hsv.x + shift);
    return hsvToRgb(hsv);
}

// OKLab color space (better perceptual hue shifting)
vec3 rgbToOklab(vec3 c) {
    float l = 0.4122214708 * c.r + 0.5363325363 * c.g + 0.0514459929 * c.b;
    float m = 0.2119034982 * c.r + 0.6806995451 * c.g + 0.1073969566 * c.b;
    float s = 0.0883024619 * c.r + 0.2817188376 * c.g + 0.6299787005 * c.b;
    
    float l_ = pow(l, 1.0/3.0);
    float m_ = pow(m, 1.0/3.0);
    float s_ = pow(s, 1.0/3.0);
    
    return vec3(
        0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
        1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
        0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_
    );
}

vec3 oklabToRgb(vec3 c) {
    float l_ = c.x + 0.3963377774 * c.y + 0.2158037573 * c.z;
    float m_ = c.x - 0.1055613458 * c.y - 0.0638541728 * c.z;
    float s_ = c.x - 0.0894841775 * c.y - 1.2914855480 * c.z;
    
    float l = l_ * l_ * l_;
    float m = m_ * m_ * m_;
    float s = s_ * s_ * s_;
    
    return vec3(
        +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
        -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
        -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s
    );
}

vec3 hueShiftOklab(vec3 color, float shift) {
    vec3 lab = rgbToOklab(color);
    float hue = atan(lab.z, lab.y);
    float chroma = length(lab.yz);
    hue += shift * TWO_PI;
    lab.y = chroma * cos(hue);
    lab.z = chroma * sin(hue);
    return oklabToRgb(lab);
}

// ============================================
// Normal Mapping
// ============================================

vec3 unpackNormal(vec4 packednormal, float scale) {
    vec3 normal;
    normal.xy = (packednormal.xy * 2.0 - 1.0) * scale;
    normal.z = sqrt(1.0 - saturate(dot(normal.xy, normal.xy)));
    return normal;
}

vec3 blendNormals(vec3 n1, vec3 n2) {
    return normalize(vec3(n1.xy + n2.xy, n1.z * n2.z));
}

mat3 calculateTBN(vec3 normal, vec3 tangent, float tangentW) {
    vec3 bitangent = cross(normal, tangent) * tangentW;
    return mat3(tangent, bitangent, normal);
}

#endif // TOON_COMMON_GLSL
