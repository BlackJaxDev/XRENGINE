#ifndef XR_LENS_DISTORTION_INCLUDED
#define XR_LENS_DISTORTION_INCLUDED

uniform int LensDistortionMode; // XRENGINE_FREQUENCY(View)
uniform float LensDistortionIntensity; // XRENGINE_FREQUENCY(View)
uniform vec2 LensDistortionCenter; // XRENGINE_FREQUENCY(View)
uniform float PaniniDistance; // XRENGINE_FREQUENCY(View)
uniform float PaniniCrop; // XRENGINE_FREQUENCY(View)
uniform vec2 PaniniViewExtents; // XRENGINE_FREQUENCY(View)
uniform vec3 BrownConradyRadial; // XRENGINE_FREQUENCY(View)
uniform vec2 BrownConradyTangential; // XRENGINE_FREQUENCY(View)

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

#endif

