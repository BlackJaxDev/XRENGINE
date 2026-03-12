#version 450

layout (location = 0) out vec4 OutAccum;
layout (location = 1) out vec4 OutRevealage;
layout (location = 4) in vec2 FragUV0;

uniform sampler2D Texture0;
uniform float AlphaThreshold = 0.1f;

float XRE_ComputeOitWeight(float alpha)
{
    float depthWeight = clamp(1.0 - gl_FragCoord.z * 0.85, 0.05, 1.0);
    return clamp(alpha * (0.25 + depthWeight * depthWeight * 4.0), 1e-2, 8.0);
}

void main()
{
    vec4 color = texture(Texture0, FragUV0);
    if (color.a < AlphaThreshold)
        discard;

    float alpha = clamp(color.a, 0.0, 1.0);
    float weight = XRE_ComputeOitWeight(alpha);
    vec3 premultiplied = color.rgb * alpha;
    OutAccum = vec4(premultiplied * weight, alpha * weight);
    OutRevealage = vec4(alpha);
}