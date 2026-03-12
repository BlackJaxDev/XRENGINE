#version 450

layout (location = 0) out vec4 OutAccum;
layout (location = 1) out vec4 OutRevealage;

uniform vec4 MatColor;
uniform float MatSpecularIntensity;
uniform float MatShininess;

uniform vec3 CameraPosition;
uniform vec3 CameraForward;

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;

#pragma snippet "ForwardLighting"

float XRE_ComputeOitWeight(float alpha)
{
    float depthWeight = clamp(1.0 - gl_FragCoord.z * 0.85, 0.05, 1.0);
    return clamp(alpha * (0.25 + depthWeight * depthWeight * 4.0), 1e-2, 8.0);
}

void XRE_WriteWeightedBlendedOit(vec4 shadedColor)
{
    float alpha = clamp(shadedColor.a, 0.0, 1.0);
    if (alpha <= 0.0001)
        discard;

    float weight = XRE_ComputeOitWeight(alpha);
    vec3 premultiplied = shadedColor.rgb * alpha;
    OutAccum = vec4(premultiplied * weight, alpha * weight);
    OutRevealage = vec4(alpha);
}

void main()
{
    vec3 normal = normalize(FragNorm);
    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, MatColor.rgb, MatSpecularIntensity, 1.0);
    XRE_WriteWeightedBlendedOit(MatColor * vec4(totalLight, 1.0));
}