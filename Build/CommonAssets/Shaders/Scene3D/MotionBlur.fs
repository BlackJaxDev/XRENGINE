#version 450 core

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;

// Sampler names must match the SamplerName property of the textures passed to the material.
uniform sampler2D MotionBlur;  // Copied scene color
uniform sampler2D Velocity;    // Velocity buffer
uniform sampler2D DepthView;   // Depth buffer view

uniform vec2 TexelSize;
uniform float ShutterScale;
uniform float VelocityThreshold;
uniform float DepthRejectThreshold;
uniform float MaxBlurPixels;
uniform float SampleFalloff;
uniform int MaxSamples;

bool IsValidUv(vec2 uv)
{
    return all(greaterThanEqual(uv, vec2(0.0f))) && all(lessThanEqual(uv, vec2(1.0f)));
}

float DepthDifference(vec2 uv, float baseDepth)
{
    return abs(texture(DepthView, uv).r - baseDepth);
}

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x > 1.0f || clipXY.y > 1.0f)
    {
        discard;
    }

    vec2 uv = clipXY * 0.5f + 0.5f;
    vec4 baseColor = texture(MotionBlur, uv);
    vec2 velocity = texture(Velocity, uv).xy;
    float depth = texture(DepthView, uv).r;

    if (MaxSamples <= 1)
    {
        OutColor = baseColor;
        return;
    }

    float motionMagnitude = length(velocity);
    if (motionMagnitude < VelocityThreshold || ShutterScale <= 0.0f)
    {
        OutColor = baseColor;
        return;
    }

    vec2 direction = normalize(velocity);
    float texelMagnitude = max(length(TexelSize), 1e-5f);
    float maxBlurUv = MaxBlurPixels * texelMagnitude;
    float blurLength = min(motionMagnitude * ShutterScale, maxBlurUv);
    if (blurLength <= 1e-5f)
    {
        OutColor = baseColor;
        return;
    }

    float blurRatio = clamp(blurLength / maxBlurUv, 0.0f, 1.0f);
    int steps = max(1, int(ceil(blurRatio * float(MaxSamples))));
    vec2 blurVector = direction * blurLength;

    vec3 accum = baseColor.rgb;
    float totalWeight = 1.0f;

    for (int i = 1; i <= steps; ++i)
    {
        float t = float(i) / float(steps);
        float weight = exp(-t * SampleFalloff);
        vec2 offset = blurVector * t;

        vec2 uvPos = uv + offset;
        if (IsValidUv(uvPos) && DepthDifference(uvPos, depth) <= DepthRejectThreshold)
        {
            accum += texture(MotionBlur, uvPos).rgb * weight;
            totalWeight += weight;
        }

        vec2 uvNeg = uv - offset;
        if (IsValidUv(uvNeg) && DepthDifference(uvNeg, depth) <= DepthRejectThreshold)
        {
            accum += texture(MotionBlur, uvNeg).rgb * weight;
            totalWeight += weight;
        }
    }

    OutColor = vec4(accum / max(totalWeight, 1e-4f), baseColor.a);
}
