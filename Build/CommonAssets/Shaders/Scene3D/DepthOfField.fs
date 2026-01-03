#version 450 core

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;

// Sampler names must match the SamplerName property of the textures passed to the material.
uniform sampler2D ColorSource; // Copied scene color
uniform sampler2D DepthView;   // Depth buffer view

uniform vec2 TexelSize;

// 0 = Artist, 1 = Physical
uniform int DoFMode;

uniform float FocusDepth;       // depth-buffer value at focus plane
uniform float FocusRangeDepth;  // depth-buffer delta spanning near/far focus band
uniform float Aperture;
uniform float MaxCoC;
uniform float BokehRadius;
uniform bool NearBlur;

// Physical DOF inputs
uniform float CameraNearZ;
uniform float CameraFarZ;
uniform float DoFPhysicalFocalLengthMm;
uniform float DoFPhysicalFocusDistanceMm;
uniform float DoFPhysicalCoCRefMm;
uniform float DoFPhysicalPixelsPerMm;

const vec2 Poisson[12] = vec2[](
    vec2( 0.0,  0.0),
    vec2( 0.527,  0.527),
    vec2(-0.527,  0.527),
    vec2( 0.527, -0.527),
    vec2(-0.527, -0.527),
    vec2( 0.0,  0.75),
    vec2( 0.0, -0.75),
    vec2( 0.75,  0.0),
    vec2(-0.75,  0.0),
    vec2( 0.35,  0.62),
    vec2(-0.62,  0.35),
    vec2( 0.62, -0.35)
);

float ComputeCoC(float depth)
{
    if (DoFMode == 1)
    {
        // Convert normalized nonlinear depth to linear distance (matches XRCamera.DepthToDistance)
        float depthSample = 2.0 * depth - 1.0;
        float zM = (2.0 * CameraNearZ * CameraFarZ) / (CameraFarZ + CameraNearZ - depthSample * (CameraFarZ - CameraNearZ));
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
        float signedDepth = depth - FocusDepth;
        if (!NearBlur && signedDepth < 0.0)
            return 0.0;

        float blur = abs(signedDepth) / max(FocusRangeDepth, 1e-5);
        float apertureScale = 0.35 + Aperture * 0.15; // map f-stop style to modest scale
        return clamp(blur * apertureScale, 0.0, 1.0) * MaxCoC;
    }
}

bool IsValidUv(vec2 uv)
{
    return all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)));
}

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x > 1.0 || clipXY.y > 1.0)
        discard;

    vec2 uv = clipXY * 0.5 + 0.5;
    float depth = texture(DepthView, uv).r;
    float coc = ComputeCoC(depth);

    // Fast path: in focus
    if (coc <= 1e-4)
    {
        OutColor = texture(ColorSource, uv);
        return;
    }

    float radius = max(coc * BokehRadius, 0.0);
    vec3 accum = texture(ColorSource, uv).rgb;
    float weightSum = 1.0;

    for (int i = 1; i < 12; ++i)
    {
        vec2 offset = Poisson[i] * radius * TexelSize;
        vec2 sampleUv = uv + offset;
        if (!IsValidUv(sampleUv))
            continue;

        float sampleDepth = texture(DepthView, sampleUv).r;
        float sampleCoc = ComputeCoC(sampleDepth);

        // Favor samples that are at least as blurred as the current pixel.
        float weight = max(sampleCoc, coc) + 1e-4;
        accum += texture(ColorSource, sampleUv).rgb * weight;
        weightSum += weight;
    }

    OutColor = vec4(accum / max(weightSum, 1e-4), 1.0);
}
