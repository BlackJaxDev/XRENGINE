#version 460
#extension GL_OVR_multiview2 : require

layout(location = 0) out vec3 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray SourceTexture;

uniform int SourceLOD;
uniform bool UseThreshold;
uniform float BloomThreshold;
uniform float BloomSoftKnee;
uniform float BloomIntensity;
uniform vec3 Luminance = vec3(0.299, 0.587, 0.114);
uniform bool UseKarisAverage;

float KarisWeight(vec3 c)
{
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
    float layer = float(gl_ViewID_OVR);
    vec2 texelSize = 1.0 / textureSize(SourceTexture, SourceLOD).xy;

    vec3 a = textureLod(SourceTexture, vec3(uv + texelSize * vec2(-1.0, -1.0), layer), lod).rgb;
    vec3 b = textureLod(SourceTexture, vec3(uv + texelSize * vec2( 0.0, -1.0), layer), lod).rgb;
    vec3 c = textureLod(SourceTexture, vec3(uv + texelSize * vec2( 1.0, -1.0), layer), lod).rgb;
    vec3 d = textureLod(SourceTexture, vec3(uv + texelSize * vec2(-0.5, -0.5), layer), lod).rgb;
    vec3 e = textureLod(SourceTexture, vec3(uv + texelSize * vec2( 0.5, -0.5), layer), lod).rgb;
    vec3 f = textureLod(SourceTexture, vec3(uv + texelSize * vec2(-1.0,  0.0), layer), lod).rgb;
    vec3 g = textureLod(SourceTexture, vec3(uv,                                layer), lod).rgb;
    vec3 h = textureLod(SourceTexture, vec3(uv + texelSize * vec2( 1.0,  0.0), layer), lod).rgb;
    vec3 i = textureLod(SourceTexture, vec3(uv + texelSize * vec2(-0.5,  0.5), layer), lod).rgb;
    vec3 j = textureLod(SourceTexture, vec3(uv + texelSize * vec2( 0.5,  0.5), layer), lod).rgb;
    vec3 k = textureLod(SourceTexture, vec3(uv + texelSize * vec2(-1.0,  1.0), layer), lod).rgb;
    vec3 l = textureLod(SourceTexture, vec3(uv + texelSize * vec2( 0.0,  1.0), layer), lod).rgb;
    vec3 m = textureLod(SourceTexture, vec3(uv + texelSize * vec2( 1.0,  1.0), layer), lod).rgb;

    vec3 result;

    if (UseKarisAverage)
    {
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
        result  = (d + e + i + j) * 0.125;
        result += (a + b + f + g) * 0.03125;
        result += (b + c + g + h) * 0.03125;
        result += (f + g + k + l) * 0.03125;
        result += (g + h + l + m) * 0.03125;
    }

    if (UseThreshold)
        result = BrightPass(result);

    OutColor = max(result, vec3(0.0));
}
