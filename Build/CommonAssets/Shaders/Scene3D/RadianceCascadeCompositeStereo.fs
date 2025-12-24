#version 450 core

layout(location = 0) out vec4 OutColor;

uniform sampler2DArray RadianceCascadeGITexture;
uniform sampler2DArray DepthView;
uniform sampler2DArray Normal;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform int ViewIndex; // 0 = left eye, 1 = right eye

const float DepthSharpness = 420.0;
const float NormalPower = 4.0;

vec3 DecodeNormal(vec3 encoded)
{
    return normalize(encoded * 2.0 - 1.0);
}

vec3 DepthAwareUpscale(vec2 uv, int eye)
{
    vec2 screenSize = vec2(ScreenWidth, ScreenHeight);
    vec2 giSize = vec2(textureSize(RadianceCascadeGITexture, 0).xy);
    if (giSize.x <= 0.0 || giSize.y <= 0.0)
        return vec3(0.0);

    vec2 scale = screenSize / giSize;
    vec2 baseUv = uv / scale;
    vec2 texel = 1.0 / giSize;

    float centerDepth = texture(DepthView, vec3(uv, float(eye))).r;
    vec3 centerNormal = DecodeNormal(texture(Normal, vec3(uv, float(eye))).rgb);

    vec2 offsets[5] = vec2[](
        vec2(0.0, 0.0),
        vec2(-1.0, 0.0),
        vec2(1.0, 0.0),
        vec2(0.0, -1.0),
        vec2(0.0, 1.0)
    );

    vec3 accum = vec3(0.0);
    float weightSum = 0.0;

    for (int i = 0; i < 5; ++i)
    {
        vec2 offset = offsets[i];
        vec2 giUv = clamp(baseUv + offset * texel, vec2(0.0), vec2(1.0));
        vec2 depthUv = clamp(uv + offset * texel * scale, vec2(0.0), vec2(1.0));

        float tapDepth = texture(DepthView, vec3(depthUv, float(eye))).r;
        vec3 tapNormal = DecodeNormal(texture(Normal, vec3(depthUv, float(eye))).rgb);

        float wDepth = exp(-abs(tapDepth - centerDepth) * DepthSharpness);
        float wNormal = pow(max(dot(centerNormal, tapNormal), 0.0), NormalPower);
        float w = max(wDepth * wNormal, 1e-4);

        vec3 tapGi = texture(RadianceCascadeGITexture, vec3(giUv, float(eye))).rgb;
        accum += tapGi * w;
        weightSum += w;
    }

    return accum / max(weightSum, 1e-4);
}

void main()
{
    if (ScreenWidth <= 0.0 || ScreenHeight <= 0.0)
    {
        OutColor = vec4(0.0);
        return;
    }

    vec2 uv = gl_FragCoord.xy / vec2(ScreenWidth, ScreenHeight);
    vec3 gi = DepthAwareUpscale(uv, ViewIndex);
    OutColor = vec4(gi, 0.0);
}
