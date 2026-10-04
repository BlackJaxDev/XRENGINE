// Exact mono browser companion of the shared Advanced output operation.
struct OutputUniforms
{
    float ScreenWidth;
    float ScreenHeight;
    float2 ScreenOrigin;
    float2 TexelSize;
    int DoFMode;
    float FocusDepth;
    float FocusRangeDepth;
    float Aperture;
    float MaxCoC;
    float BokehRadius;
    uint NearBlur;
    float CameraNearZ;
    float CameraFarZ;
    float DoFPhysicalFocalLengthMm;
    float DoFPhysicalFocusDistanceMm;
    float DoFPhysicalCoCRefMm;
    float DoFPhysicalPixelsPerMm;
    int DepthMode;
};
[[vk::binding(0, 0)]] ConstantBuffer<OutputUniforms> outputUniforms;
[[vk::binding(0, 1)]] Texture2D<float4> ColorSource;
[[vk::binding(1, 1)]] SamplerState ColorSourceSampler;
[[vk::binding(2, 1)]] DepthTexture2D DepthView;

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
#define TexelSize outputUniforms.TexelSize
#define DoFMode outputUniforms.DoFMode
#define FocusDepth outputUniforms.FocusDepth
#define FocusRangeDepth outputUniforms.FocusRangeDepth
#define Aperture outputUniforms.Aperture
#define MaxCoC outputUniforms.MaxCoC
#define BokehRadius outputUniforms.BokehRadius
#define NearBlur (outputUniforms.NearBlur != 0u)
#define CameraNearZ outputUniforms.CameraNearZ
#define CameraFarZ outputUniforms.CameraFarZ
#define DoFPhysicalFocalLengthMm outputUniforms.DoFPhysicalFocalLengthMm
#define DoFPhysicalFocusDistanceMm outputUniforms.DoFPhysicalFocusDistanceMm
#define DoFPhysicalCoCRefMm outputUniforms.DoFPhysicalCoCRefMm
#define DoFPhysicalPixelsPerMm outputUniforms.DoFPhysicalPixelsPerMm
#define DepthMode outputUniforms.DepthMode

float ReadDepth(float2 uv)
{
    uint width, height; DepthView.GetDimensions(width, height);
    int2 pixel = min(int2(saturate(uv) * float2(width, height)), int2(width, height) - 1);
    return DepthView.Load(int3(pixel, 0));
}
static const float2 Poisson[12] = {
    float2( 0.0,  0.0),
    float2( 0.527,  0.527),
    float2(-0.527,  0.527),
    float2( 0.527, -0.527),
    float2(-0.527, -0.527),
    float2( 0.0,  0.75),
    float2( 0.0, -0.75),
    float2( 0.75,  0.0),
    float2(-0.75,  0.0),
    float2( 0.35,  0.62),
    float2(-0.62,  0.35),
    float2( 0.62, -0.35)
};

float ComputeCoC(float depth)
{
    if (DoFMode == 1)
    {
        // Convert normalized nonlinear depth to linear distance (matches XRCamera.DepthToDistance)
        float normalDepth = DepthMode == 1 ? 1.0 - depth : depth;
        float zM = CameraNearZ * CameraFarZ / (CameraFarZ - normalDepth * (CameraFarZ - CameraNearZ));
        float zMm = zM * 1000.0;

        float sMm = max(DoFPhysicalFocusDistanceMm, 0.001);
        float fMm = max(DoFPhysicalFocalLengthMm, 0.001);
        float n = max(Aperture, 0.1); // f-number

        // Physical circle of confusion diameter on sensor (mm):
        // c = | f^2 / (N (s - f)) * (z - s) / z |
        float denom = max(n * (sMm - fMm), 1e-6);
        float cMm = abs((fMm * fMm) / denom * ((zMm - sMm) / max(zMm, 1e-6)));

        float cocRef = max(DoFPhysicalCoCRefMm, 1e-6);
        if (cMm <= cocRef)
            return 0.0;

        // Optional: disable near blur by comparing to focus distance.
        if (!NearBlur && zMm < sMm)
            return 0.0;

        // Convert to pixel radius, capped by MaxCoC (px)
        float cocPx = (cMm - cocRef) * 0.5 * DoFPhysicalPixelsPerMm;
        return min(cocPx, MaxCoC);
    }
    else
    {
        float signedDepth = (depth - FocusDepth) * (DepthMode == 1 ? -1.0 : 1.0);
        if (!NearBlur && signedDepth < 0.0)
            return 0.0;

        float blur = abs(signedDepth) / max(FocusRangeDepth, 1e-5);
        float apertureScale = 0.35 + Aperture * 0.15; // map f-stop style to modest scale
        return clamp(blur * apertureScale, 0.0, 1.0) * MaxCoC;
    }
}

bool IsValidUv(float2 uv)
{
    return all(uv >= float2(0.0)) && all(uv <= float2(1.0));
}

[shader("fragment")]
float4 outputFragment(ScreenOutput input) : SV_Target0
{
    float2 uv = clamp(
        (input.position.xy - ScreenOrigin) / max(float2(ScreenWidth, ScreenHeight), float2(1.0)),
        float2(0.0),
        float2(1.0));
    float depth = ReadDepth(uv);
    float coc = ComputeCoC(depth);

    // Fast path: in focus
    if (coc <= 1e-4)
    {
        return ColorSource.SampleLevel(ColorSourceSampler, uv, 0.0);
    }

    float radius = max(coc * BokehRadius, 0.0);
    float3 accum = ColorSource.SampleLevel(ColorSourceSampler, uv, 0.0).rgb;
    float weightSum = 1.0;

    for (int i = 1; i < 12; ++i)
    {
        float2 offset = Poisson[i] * radius * TexelSize;
        float2 sampleUv = uv + offset;
        if (!IsValidUv(sampleUv))
            continue;

        float sampleDepth = ReadDepth(sampleUv);
        float sampleCoc = ComputeCoC(sampleDepth);

        // Favor samples that are at least as blurred as the current pixel.
        float weight = max(sampleCoc, coc) + 1e-4;
        accum += ColorSource.SampleLevel(ColorSourceSampler, sampleUv, 0.0).rgb * weight;
        weightSum += weight;
    }

    return float4(accum / max(weightSum, 1e-4), 1.0);
}
