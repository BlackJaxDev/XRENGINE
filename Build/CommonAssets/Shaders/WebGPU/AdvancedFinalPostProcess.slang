// Exact mono browser companion of the shared Advanced output operation.
struct OutputUniforms
{
    float ScreenWidth;
    float ScreenHeight;
    float2 ScreenOrigin;
    int LensDistortionMode;
    float LensDistortionIntensity;
    float2 LensDistortionCenter;
    float PaniniDistance;
    float PaniniCrop;
    float2 PaniniViewExtents;
    float3 BrownConradyRadial;
    float2 BrownConradyTangential;
};
[[vk::binding(0, 0)]] ConstantBuffer<OutputUniforms> outputUniforms;
[[vk::binding(0, 1)]] Texture2D<float4> PostProcessOutputTexture;
[[vk::binding(1, 1)]] SamplerState PostProcessOutputTextureSampler;

struct ScreenInput { [[vk::location(0)]] float3 position : POSITION; };
struct ScreenOutput { float4 position : SV_Position; };
[shader("vertex")]
ScreenOutput outputVertex(ScreenInput input)
{
    ScreenOutput output; output.position = float4(input.position.xy, 0.0, 1.0); return output;
}
#define ScreenWidth outputUniforms.ScreenWidth
#define ScreenHeight outputUniforms.ScreenHeight
#define ScreenOrigin outputUniforms.ScreenOrigin
#define LensDistortionMode outputUniforms.LensDistortionMode
#define LensDistortionIntensity outputUniforms.LensDistortionIntensity
#define LensDistortionCenter outputUniforms.LensDistortionCenter
#define PaniniDistance outputUniforms.PaniniDistance
#define PaniniCrop outputUniforms.PaniniCrop
#define PaniniViewExtents outputUniforms.PaniniViewExtents
#define BrownConradyRadial outputUniforms.BrownConradyRadial
#define BrownConradyTangential outputUniforms.BrownConradyTangential

float2 ApplyLensDistortion(float2 uv, float intensity, float2 center)
{
    uv -= center;
    float uva = atan2(uv.x, uv.y);
    float uvd = sqrt(dot(uv, uv));
    uvd *= 1.0 + intensity * uvd * uvd;
    return center + float2(sin(uva), cos(uva)) * uvd;
}

float2 ApplyBrownConrady(float2 uvCentered)
{
    float2 x = uvCentered * 2.0 - 1.0;
    float r2 = dot(x, x);
    float r4 = r2 * r2;
    float r6 = r4 * r2;

    float radial = 1.0
        + BrownConradyRadial.x * r2
        + BrownConradyRadial.y * r4
        + BrownConradyRadial.z * r6;

    float2 tangential = float2(
        2.0 * BrownConradyTangential.x * x.x * x.y + BrownConradyTangential.y * (r2 + 2.0 * x.x * x.x),
        BrownConradyTangential.x * (r2 + 2.0 * x.y * x.y) + 2.0 * BrownConradyTangential.y * x.x * x.y);

    return (x * radial + tangential) * 0.5 + 0.5;
}

float2 ApplyPaniniProjection(float2 viewPos, float distance)
{
    float viewDistance = 1.0 + distance;
    float viewHypSq = viewPos.x * viewPos.x + viewDistance * viewDistance;
    float isectD = viewPos.x * distance;
    float isectDiscrim = viewHypSq - isectD * isectD;
    float cylDistMinusD = (-isectD * viewPos.x + viewDistance * sqrt(max(isectDiscrim, 0.0))) / viewHypSq;
    float cylDist = cylDistMinusD + distance;
    float2 cylPos = viewPos * (cylDist / viewDistance);
    return cylPos / (cylDist - distance);
}

float2 ApplyLensDistortionByMode(float2 uv)
{
    float2 uvCentered = uv - LensDistortionCenter + float2(0.5);

    if (LensDistortionMode == 1 || LensDistortionMode == 2)
    {
        if (LensDistortionIntensity != 0.0)
            return ApplyLensDistortion(uvCentered, LensDistortionIntensity, float2(0.5));
    }
    else if (LensDistortionMode == 3)
    {
        if (PaniniDistance > 0.0)
        {
            float2 viewPos = (2.0 * uvCentered - 1.0) * PaniniViewExtents * PaniniCrop;
            float2 projPos = ApplyPaniniProjection(viewPos, PaniniDistance);
            float2 projNdc = projPos / PaniniViewExtents;
            return projNdc * 0.5 + LensDistortionCenter;
        }
    }
    else if (LensDistortionMode == 4)
    {
        return ApplyBrownConrady(uvCentered) - float2(0.5) + LensDistortionCenter;
    }

    return uv;
}

[shader("fragment")]
float4 outputFragment(ScreenOutput input) : SV_Target0
{

    float2 uv = clamp(
        (input.position.xy - ScreenOrigin) / max(float2(ScreenWidth, ScreenHeight), float2(1.0)),
        float2(0.0),
        float2(1.0));
    float2 sourceUv = clamp(ApplyLensDistortionByMode(uv), float2(0.0), float2(1.0));
    return PostProcessOutputTexture.SampleLevel(PostProcessOutputTextureSampler, sourceUv, 0.0);
}
