#version 460

#pragma snippet "DepthUtils"

layout(location = 0) out vec4 OutAccum;
layout(location = 1) out vec4 OutRevealage;

uniform sampler2D DepthView;
uniform sampler2D Texture4; // Decal AlbedoOpacity

uniform float ScreenWidth;
uniform float ScreenHeight;
uniform mat4 InverseProjMatrix;
uniform mat4 ProjMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 BoxWorldMatrix;
uniform vec3 BoxHalfScale;

float XRE_ComputeOitWeight(float alpha)
{
    float depthWeight = clamp(1.0 - gl_FragCoord.z * 0.85, 0.05, 1.0);
    return clamp(alpha * (0.25 + depthWeight * depthWeight * 4.0), 1e-2, 8.0);
}

void main()
{
    vec2 uv = gl_FragCoord.xy / vec2(ScreenWidth, ScreenHeight);

    float depth = texture(DepthView, uv).r;

    // Reconstruct world-space position from depth
    vec3 fragPosWS = XRENGINE_WorldPosFromDepthRaw(depth, uv, InverseProjMatrix, InverseViewMatrix);
    vec4 fragPosOS = (inverse(BoxWorldMatrix) * vec4(fragPosWS, 1.0f));
    fragPosOS.xyz /= BoxHalfScale;

    // Reject fragments outside the decal projection box
    if (abs(fragPosOS.x) > 1.0f ||
        abs(fragPosOS.y) > 1.0f ||
        abs(fragPosOS.z) > 1.0f)
        discard;

    vec2 decalUV = fragPosOS.xz * vec2(0.5f) + vec2(0.5f);
    float intensity = smoothstep(0.0f, 1.0f, 1.0f - abs(fragPosOS.y));

    vec4 decalColor = texture(Texture4, decalUV);
    float alpha = clamp(decalColor.a * intensity, 0.0, 1.0);
    if (alpha <= 0.0001)
        discard;

    float weight = XRE_ComputeOitWeight(alpha);
    vec3 premultiplied = decalColor.rgb * alpha;
    OutAccum = vec4(premultiplied * weight, alpha * weight);
    OutRevealage = vec4(alpha);
}
