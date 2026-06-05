#version 450
#pragma snippet "ScreenSpaceUtils"

layout (location = 0) out vec4 OutColor;

uniform int MaxSteps;
uniform float StepSize;
uniform float MaxDistance;
uniform float Thickness;

uniform sampler2D Texture0;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform int SampleCount;

const float pi = 3.14159265359f;

uniform vec4 MatColor;
uniform float BlurStrength;
uniform vec3 PlaneNormal;
uniform vec3 CameraForward;
uniform mat4 ProjMatrix;

float gaussian(float x, float sigma)
{
    return exp(-0.5f * (x * x) / (sigma * sigma)) / (sigma * sqrt(2.0f * pi));
}

float rayMarch(vec3 ro, vec3 rd, out vec3 hitPos)
{
    float t = 0.0f;
    for (int i = 0; i < MaxSteps; i++)
    {
        vec3 p = ro + t * rd;
        vec4 projP = ProjMatrix * vec4(p, 1.0);
        vec2 uv = projP.xy / projP.w * 0.5 + 0.5;
        float d = texture(Texture0, uv).r - p.z;
        if (d < Thickness)
        {
            hitPos = p;
            return t;
        }
        t += StepSize;
        if (t > MaxDistance)
            break;
    }
    hitPos = vec3(0.0);
    return -1.0;
}

vec3 reflectScreenSpace(vec3 viewDir, vec3 normal, vec2 texCoord)
{
    vec3 reflectedDir = reflect(viewDir, normal);
    vec3 ro = vec3(texCoord, 0.0f);
    vec3 hitPos;
    float t = rayMarch(ro, reflectedDir, hitPos);
    if (t > 0.0f)
        return texture(Texture0, hitPos.xy).rgb;
    return vec3(0.0f);
}

void main()
{
    float xOffset = 1.0f / ScreenWidth;
    float yOffset = 1.0f / ScreenHeight;
    vec2 vTexCoord = XRENGINE_ScreenUV(gl_FragCoord.xy, vec2(ScreenWidth, ScreenHeight));
    vec3 col = texture(Texture0, vTexCoord).rgb * gaussian(0.0, BlurStrength);

    for (int i = 1; i < SampleCount; i++)
    {
        float offset = float(i) * BlurStrength;
        col += texture(Texture0, vTexCoord + vec2(xOffset * offset, 0.0f)).rgb * gaussian(offset, BlurStrength);
        col += texture(Texture0, vTexCoord - vec2(xOffset * offset, 0.0f)).rgb * gaussian(offset, BlurStrength);
        col += texture(Texture0, vTexCoord + vec2(0.0f, yOffset * offset)).rgb * gaussian(offset, BlurStrength);
        col += texture(Texture0, vTexCoord - vec2(0.0f, yOffset * offset)).rgb * gaussian(offset, BlurStrength);
    }

    vec3 viewDir = normalize(CameraForward);
    vec3 reflection = reflectScreenSpace(viewDir, PlaneNormal, vTexCoord);
    col = mix(col, reflection, 0.5); // Blend the reflection with the original color

    OutColor = vec4(col, 1.0f) * MatColor;
}
