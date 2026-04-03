#version 450

layout(location = 0) out vec3 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;

uniform int SourceLOD;           // Mip level to read from
uniform bool UseThreshold;       // Apply bright-pass threshold (first downsample only)
uniform float BloomThreshold;
uniform float BloomSoftKnee;
uniform float BloomIntensity;
uniform vec3 Luminance = vec3(0.299, 0.587, 0.114);
uniform bool UseKarisAverage;    // Anti-firefly weighting (first downsample only)

// 13-tap downsample filter (Jimenez 2014 / COD: Advanced Warfare)
// Produces a high-quality 2x downsample with a wide tent footprint,
// eliminating the aliasing and pulsing artifacts of hardware GenerateMipmaps.

float KarisWeight(vec3 c)
{
    // Weight by inverse brightness to suppress firefly contribution.
    return 1.0 / (1.0 + dot(c, Luminance));
}

vec3 BrightPass(vec3 c)
{
    float brightness = dot(c, Luminance);
    if (brightness <= 1e-5)
        return vec3(0.0);
    float knee = max(BloomThreshold * BloomSoftKnee, 1e-5);
    float soft = clamp(brightness - BloomThreshold + knee, 0.0, 2.0 * knee);
    soft = (soft * soft) / (4.0 * knee + 1e-5);
    float contribution = max(soft, brightness - BloomThreshold);
    return c * (contribution / brightness) * BloomIntensity;
}

void main()
{
    vec2 uv = clamp(FragPos.xy, -1.0, 1.0) * 0.5 + 0.5;
    uv = clamp(uv, 0.0, 1.0);

    float lod = float(SourceLOD);
    vec2 texelSize = 1.0 / textureSize(SourceTexture, SourceLOD);

    // Sample the 13 taps covering a 4x4 source texel neighborhood.
    //
    //  a . b . c
    //  . d . e .
    //  f . g . h
    //  . i . j .
    //  k . l . m
    //
    // Each letter is a bilinear tap. Five overlapping 2x2 groups
    // sum to an energy-preserving tent filter.

    vec3 a = textureLod(SourceTexture, uv + texelSize * vec2(-1.0, -1.0), lod).rgb;
    vec3 b = textureLod(SourceTexture, uv + texelSize * vec2( 0.0, -1.0), lod).rgb;
    vec3 c = textureLod(SourceTexture, uv + texelSize * vec2( 1.0, -1.0), lod).rgb;
    vec3 d = textureLod(SourceTexture, uv + texelSize * vec2(-0.5, -0.5), lod).rgb;
    vec3 e = textureLod(SourceTexture, uv + texelSize * vec2( 0.5, -0.5), lod).rgb;
    vec3 f = textureLod(SourceTexture, uv + texelSize * vec2(-1.0,  0.0), lod).rgb;
    vec3 g = textureLod(SourceTexture, uv,                                lod).rgb;
    vec3 h = textureLod(SourceTexture, uv + texelSize * vec2( 1.0,  0.0), lod).rgb;
    vec3 i = textureLod(SourceTexture, uv + texelSize * vec2(-0.5,  0.5), lod).rgb;
    vec3 j = textureLod(SourceTexture, uv + texelSize * vec2( 0.5,  0.5), lod).rgb;
    vec3 k = textureLod(SourceTexture, uv + texelSize * vec2(-1.0,  1.0), lod).rgb;
    vec3 l = textureLod(SourceTexture, uv + texelSize * vec2( 0.0,  1.0), lod).rgb;
    vec3 m = textureLod(SourceTexture, uv + texelSize * vec2( 1.0,  1.0), lod).rgb;

    vec3 result;

    if (UseKarisAverage)
    {
        // Partial Karis average: weight each 2x2 group by inverse brightness
        // to prevent isolated bright pixels from dominating the downsample.
        vec3 g0 = (d + e + i + j);
        vec3 g1 = (a + b + f + g);
        vec3 g2 = (b + c + g + h);
        vec3 g3 = (f + g + k + l);
        vec3 g4 = (g + h + l + m);

        float w0 = KarisWeight(g0 * 0.25);
        float w1 = KarisWeight(g1 * 0.25);
        float w2 = KarisWeight(g2 * 0.25);
        float w3 = KarisWeight(g3 * 0.25);
        float w4 = KarisWeight(g4 * 0.25);

        // Center group gets 0.5, corners get 0.125 each (energy-preserving weights).
        result = g0 * 0.125 * w0
               + g1 * 0.03125 * w1
               + g2 * 0.03125 * w2
               + g3 * 0.03125 * w3
               + g4 * 0.03125 * w4;

        float wSum = 0.5 * w0 + 0.125 * (w1 + w2 + w3 + w4);
        result /= (wSum + 1e-5);
    }
    else
    {
        // Standard 13-tap weights (sums to 1.0).
        //   Center 2x2 group: weight 0.5  (4 taps at 0.125 each)
        //   Four corner 2x2 groups: weight 0.125 each (4 taps at 0.03125 each)
        result  = (d + e + i + j) * 0.125;       // center group: 0.5
        result += (a + b + f + g) * 0.03125;      // top-left: 0.125
        result += (b + c + g + h) * 0.03125;      // top-right: 0.125
        result += (f + g + k + l) * 0.03125;      // bottom-left: 0.125
        result += (g + h + l + m) * 0.03125;      // bottom-right: 0.125
    }

    // Optionally apply bright-pass threshold (first downsample only).
    if (UseThreshold)
        result = BrightPass(result);

    OutColor = max(result, vec3(0.0));
}
