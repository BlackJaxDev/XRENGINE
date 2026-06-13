#version 450

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D PostProcessOutputTexture;

uniform int LensDistortionMode;
uniform float LensDistortionIntensity;
uniform vec2 LensDistortionCenter;
uniform float PaniniDistance;
uniform float PaniniCrop;
uniform vec2 PaniniViewExtents;
uniform vec3 BrownConradyRadial;
uniform vec2 BrownConradyTangential;

vec2 ApplyLensDistortion(vec2 uv, float intensity, vec2 center)
{
    uv -= center;
    float uva = atan(uv.x, uv.y);
    float uvd = sqrt(dot(uv, uv));
    uvd *= 1.0 + intensity * uvd * uvd;
    return center + vec2(sin(uva), cos(uva)) * uvd;
}

vec2 ApplyBrownConrady(vec2 uvCentered)
{
    vec2 x = uvCentered * 2.0 - 1.0;
    float r2 = dot(x, x);
    float r4 = r2 * r2;
    float r6 = r4 * r2;

    float radial = 1.0
        + BrownConradyRadial.x * r2
        + BrownConradyRadial.y * r4
        + BrownConradyRadial.z * r6;

    vec2 tangential = vec2(
        2.0 * BrownConradyTangential.x * x.x * x.y + BrownConradyTangential.y * (r2 + 2.0 * x.x * x.x),
        BrownConradyTangential.x * (r2 + 2.0 * x.y * x.y) + 2.0 * BrownConradyTangential.y * x.x * x.y);

    return (x * radial + tangential) * 0.5 + 0.5;
}

vec2 ApplyPaniniProjection(vec2 viewPos, float distance)
{
    float viewDistance = 1.0 + distance;
    float viewHypSq = viewPos.x * viewPos.x + viewDistance * viewDistance;
    float isectD = viewPos.x * distance;
    float isectDiscrim = viewHypSq - isectD * isectD;
    float cylDistMinusD = (-isectD * viewPos.x + viewDistance * sqrt(max(isectDiscrim, 0.0))) / viewHypSq;
    float cylDist = cylDistMinusD + distance;
    vec2 cylPos = viewPos * (cylDist / viewDistance);
    return cylPos / (cylDist - distance);
}

vec2 ApplyLensDistortionByMode(vec2 uv)
{
    vec2 uvCentered = uv - LensDistortionCenter + vec2(0.5);

    if (LensDistortionMode == 1 || LensDistortionMode == 2)
    {
        if (LensDistortionIntensity != 0.0)
            return ApplyLensDistortion(uvCentered, LensDistortionIntensity, vec2(0.5));
    }
    else if (LensDistortionMode == 3)
    {
        if (PaniniDistance > 0.0)
        {
            vec2 viewPos = (2.0 * uvCentered - 1.0) * PaniniViewExtents * PaniniCrop;
            vec2 projPos = ApplyPaniniProjection(viewPos, PaniniDistance);
            vec2 projNdc = projPos / PaniniViewExtents;
            return projNdc * 0.5 + LensDistortionCenter;
        }
    }
    else if (LensDistortionMode == 4)
    {
        return ApplyBrownConrady(uvCentered) - vec2(0.5) + LensDistortionCenter;
    }

    return uv;
}

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x > 1.0 || clipXY.y > 1.0)
        discard;

    vec2 uv = XRENGINE_ClipXYToScreenUV(clipXY);
    vec2 sourceUv = clamp(ApplyLensDistortionByMode(uv), vec2(0.0), vec2(1.0));
    OutColor = texture(PostProcessOutputTexture, sourceUv);
}
