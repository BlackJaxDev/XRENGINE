#version 450

layout(location = 0) out vec4 OutColor;

layout (location = 0) in vec3 FragPos;
layout (location = 4) in vec2 FragUV0;

uniform vec3 LineColor;
uniform vec3 BGColor;
uniform float Scale;
uniform float LineWidth;
uniform vec2 Translation;
uniform float XYIncrement;

void main()
{
    float screenIncrement = Scale * XYIncrement; //Scale increment to screen space
    vec2 posVecWorld = FragPos.xy - Translation; //Vector from the world translation origin to the current pixel world position
    vec2 scaledUV = posVecWorld / screenIncrement;
    vec2 fractUV = fract(scaledUV);
    fractUV = fractUV * 2.0f - 1.0f; //scale and bias [0 to 1] to [-1 to 1]
    float width = LineWidth / screenIncrement;
    vec2 absDist = abs(fractUV);
    vec2 lines = vec2(floor(absDist + width));
    float lerp = clamp(lines.x + lines.y, 0.0f, 1.0f);

    //vec2 vigUV = absDist * (1.0f - absDist.yx);
    //float vig = clamp(pow(vigUV.x * vigUV.y * 10.0f, 1.0f), 0.0f, 1.0f);

    vec3 col = mix(BGColor, LineColor, lerp);
    OutColor = vec4(col, 1.0f);
}
