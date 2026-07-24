using XREngine.Extensions;
using ImageMagick;
using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.OpenGL.Extensions.NV;
using Silk.NET.OpenGL.Extensions.OVR;
using Silk.NET.OpenGLES.Extensions.EXT;
using Silk.NET.OpenGLES.Extensions.NV;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.UI;
using XREngine.Rendering.Shaders.Generator;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using XREngine.Components;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private byte[] _asyncBuffer = XRTexture.AllocateBytes(16, 1, EPixelFormat.Rgba, EPixelType.Float);
    private static byte[] _rgbDataForAsync(ref byte[] buffer)
    {
        if (buffer.Length < 16)
            buffer = XRTexture.AllocateBytes(16, 1, EPixelFormat.Rgba, EPixelType.Float);
        return buffer;
    }

    // Cached resources for CalcDotLuminanceFrontAsync to avoid per-frame allocations
    private uint _luminanceFrontTex;
    private uint _luminanceFrontFbo;
    private uint _luminanceFrontTexWidth;
    private uint _luminanceFrontTexHeight;
    private int _luminanceFrontMipLevels;
    private uint _luminanceFrontPbo;
    private uint _luminanceFrontPboSize;

    // Extension / capability support flags (cached after first check)
    private bool? _hasTextureFilterMinmax;
    private bool HasTextureFilterMinmax => _hasTextureFilterMinmax ??= IsExtensionSupported("GL_ARB_texture_filter_minmax");

    // Auto exposure compute support is dependent on the active GL context version/extensions.
    // Cache the result so we don't repeatedly probe/attempt compilation after a failure.
    private bool? _supportsGpuAutoExposure;

    // Compute shader for parallel luminance reduction
    private XRRenderProgram? _luminanceComputeProgram;
    private uint _luminanceResultBuffer;
    private uint _luminanceResultBufferSize;
    private bool _luminanceComputeInitialized;

    // Compute shaders for GPU auto exposure (writes exposure into a 1x1 R32F texture)
    private XRRenderProgram? _autoExposureComputeProgram2D;
    private XRRenderProgram? _autoExposureComputeProgram2DArray;
    private bool _autoExposureComputeInitialized;

    private const string LuminanceComputeShaderSource = @"
    #version 430

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(binding = 0) uniform sampler2D inputTexture;
layout(std430, binding = 0) buffer ResultBuffer {
    vec4 result;
};

uniform ivec2 textureSize;
uniform vec3 luminanceWeights;

shared vec4 sharedAccum[256];

void main() {
    uint localIdx = gl_LocalInvocationID.x + gl_LocalInvocationID.y * 16u;
    ivec2 gid = ivec2(gl_GlobalInvocationID.xy);
    
    vec4 accum = vec4(0.0);
    if (gid.x < textureSize.x && gid.y < textureSize.y) {
    vec2 uv = (vec2(gid) + 0.5) / vec2(textureSize);
    accum = textureLod(inputTexture, uv, 0.0);
    }
    sharedAccum[localIdx] = accum;
    
    barrier();
    
    // Parallel reduction within workgroup
    for (uint stride = 128u; stride > 0u; stride >>= 1u) {
    if (localIdx < stride) {
        sharedAccum[localIdx] += sharedAccum[localIdx + stride];
    }
    barrier();
    }
    
    // First thread in workgroup atomically adds to result
    if (localIdx == 0u) {
    uint pixelCount = uint(textureSize.x * textureSize.y);
    vec4 avg = sharedAccum[0] / float(pixelCount);
    result = avg;
    }
}
";

    private const string AutoExposureComputeShaderSource2D = @"
    #version 430

layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout(binding = 0) uniform sampler2D SourceTex;
layout(r32f, binding = 0) uniform image2D ExposureOut;

uniform int SmallestMip;
uniform vec3 LuminanceWeights;
uniform float AutoExposureBias;
uniform float AutoExposureScale;
uniform float ExposureDividend;
uniform float MinExposure;
uniform float MaxExposure;
uniform float ExposureBase;
uniform float FallbackExposure;
uniform float ExposureTransitionSpeed;

uniform int MeteringMode;
uniform int MeteringMip;
uniform float IgnoreTopPercent;
uniform float CenterWeightStrength;
uniform float CenterWeightPower;

const int MAX_METERING_SAMPLES = 256;

vec3 SafeRgb(vec3 rgb)
{
    rgb = max(rgb, vec3(0.0));
    if (any(isnan(rgb)) || any(isinf(rgb)))
    rgb = vec3(0.0);
    return rgb;
}

float SafeLum(vec3 rgb)
{
    rgb = SafeRgb(rgb);
    return dot(rgb, LuminanceWeights);
}

vec3 FetchRgbAt(ivec2 coord, int mip)
{
    return SafeRgb(texelFetch(SourceTex, coord, mip).rgb);
}

float ComputeMeteredLuminance()
{
    if (MeteringMode == 0)
    {
    // Current behavior: 1x1 smallest mip.
    return SafeLum(FetchRgbAt(ivec2(0, 0), SmallestMip));
    }

    int mip = clamp(MeteringMip, 0, SmallestMip);
    ivec2 ts = textureSize(SourceTex, mip);
    int w = max(1, ts.x);
    int h = max(1, ts.y);
    int total = w * h;
    int sampleCount = min(MAX_METERING_SAMPLES, total);
    int stride = max(1, total / sampleCount);

    if (MeteringMode == 1)
    {
    // Log-average (geometric mean) luminance.
    float sumLog = 0.0;
    for (int i = 0; i < sampleCount; ++i)
    {
        int idx = i * stride;
        int x = idx % w;
        int y = idx / w;
        float lum = SafeLum(FetchRgbAt(ivec2(x, y), mip));
        sumLog += log(max(lum, 1e-6));
    }
    return exp(sumLog / float(max(sampleCount, 1)));
    }

    if (MeteringMode == 2)
    {
    // Center-weighted luminance.
    float sum = 0.0;
    float weightSum = 0.0;
    float invW = 1.0 / float(w);
    float invH = 1.0 / float(h);
    float strength = clamp(CenterWeightStrength, 0.0, 1.0);
    float power = max(CenterWeightPower, 0.1);

    for (int i = 0; i < sampleCount; ++i)
    {
        int idx = i * stride;
        int x = idx % w;
        int y = idx / w;
        float lum = SafeLum(FetchRgbAt(ivec2(x, y), mip));

        float u = (float(x) + 0.5) * invW;
        float v = (float(y) + 0.5) * invH;
        float dx = u - 0.5;
        float dy = v - 0.5;
        float r = sqrt(dx * dx + dy * dy) / 0.70710678;
        float center = pow(clamp(1.0 - r, 0.0, 1.0), power);
        float wgt = mix(1.0, center, strength);

        sum += lum * wgt;
        weightSum += wgt;
    }

    return sum / max(weightSum, 1e-6);
    }

    // Ignore-top-percent luminance (approx, sorted samples).
    float lums[MAX_METERING_SAMPLES];
    for (int i = 0; i < sampleCount; ++i)
    {
    int idx = i * stride;
    int x = idx % w;
    int y = idx / w;
    lums[i] = SafeLum(FetchRgbAt(ivec2(x, y), mip));
    }

    for (int i = 1; i < sampleCount; ++i)
    {
    float key = lums[i];
    int j = i - 1;
    while (j >= 0 && lums[j] > key)
    {
        lums[j + 1] = lums[j];
        j--;
    }
    lums[j + 1] = key;
    }

    float drop = clamp(IgnoreTopPercent, 0.0, 0.5);
    int keep = int(floor((1.0 - drop) * float(sampleCount)));
    keep = clamp(keep, 1, sampleCount);

    float sum = 0.0;
    for (int i = 0; i < keep; ++i)
    sum += lums[i];
    return sum / float(keep);
}

void main()
{
    float lumDot = ComputeMeteredLuminance();

    // Clamp extremely bright outliers so they can't force exposure to MinExposure.
    // Solve for lumDot such that target == MinExposure:
    // MinExposure = (Bias + Scale * (Dividend / lumDot)) * ExposureBase
    // => Bias + Scale*(Dividend/lumDot) = MinExposure/ExposureBase
    // => lumDot = (Dividend * Scale) / max(MinExposure/ExposureBase - Bias, eps)
    float denom = max((MinExposure / max(ExposureBase, 1e-6)) - AutoExposureBias, 1e-6);
    float maxLumForMinExposure = (ExposureDividend * max(AutoExposureScale, 0.0)) / denom;
    lumDot = min(lumDot, maxLumForMinExposure);

    float current = imageLoad(ExposureOut, ivec2(0, 0)).r;
    bool currentValid = !(isnan(current) || isinf(current)) && current >= MinExposure && current <= MaxExposure;
    float stableCurrent = currentValid ? current : clamp(FallbackExposure, MinExposure, MaxExposure);

    if (!(lumDot > 0.0) || isnan(lumDot) || isinf(lumDot))
    {
    imageStore(ExposureOut, ivec2(0, 0), vec4(stableCurrent, 0.0, 0.0, 0.0));
    return;
    }

    float target = ExposureDividend / lumDot;
    target = AutoExposureBias + AutoExposureScale * target;
    target *= ExposureBase;
    target = clamp(target, MinExposure, MaxExposure);

    if (isnan(target) || isinf(target))
    {
    imageStore(ExposureOut, ivec2(0, 0), vec4(stableCurrent, 0.0, 0.0, 0.0));
    return;
    }

    float outExposure = mix(stableCurrent, target, clamp(ExposureTransitionSpeed, 0.0, 1.0));

    imageStore(ExposureOut, ivec2(0, 0), vec4(outExposure, 0.0, 0.0, 0.0));
}
";

    private const string AutoExposureComputeShaderSource2DArray = @"
    #version 430

layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout(binding = 0) uniform sampler2DArray SourceTex;
layout(r32f, binding = 0) uniform image2D ExposureOut;

uniform int SmallestMip;
uniform int LayerCount;
uniform vec3 LuminanceWeights;
uniform float AutoExposureBias;
uniform float AutoExposureScale;
uniform float ExposureDividend;
uniform float MinExposure;
uniform float MaxExposure;
uniform float ExposureBase;
uniform float FallbackExposure;
uniform float ExposureTransitionSpeed;

uniform int MeteringMode;
uniform int MeteringMip;
uniform float IgnoreTopPercent;
uniform float CenterWeightStrength;
uniform float CenterWeightPower;

const int MAX_METERING_SAMPLES = 256;

vec3 SafeRgb(vec3 rgb)
{
    rgb = max(rgb, vec3(0.0));
    if (any(isnan(rgb)) || any(isinf(rgb)))
    rgb = vec3(0.0);
    return rgb;
}

float SafeLum(vec3 rgb)
{
    rgb = SafeRgb(rgb);
    return dot(rgb, LuminanceWeights);
}

vec3 FetchRgbAt(ivec2 coord, int mip)
{
    vec3 rgb = SafeRgb(texelFetch(SourceTex, ivec3(coord, 0), mip).rgb);
    if (LayerCount > 1)
    {
    vec3 rgb1 = SafeRgb(texelFetch(SourceTex, ivec3(coord, 1), mip).rgb);
    rgb = 0.5 * (rgb + rgb1);
    }
    return rgb;
}

float ComputeMeteredLuminance()
{
    if (MeteringMode == 0)
    {
    return SafeLum(FetchRgbAt(ivec2(0, 0), SmallestMip));
    }

    int mip = clamp(MeteringMip, 0, SmallestMip);
    ivec3 ts3 = textureSize(SourceTex, mip);
    int w = max(1, ts3.x);
    int h = max(1, ts3.y);
    int total = w * h;
    int sampleCount = min(MAX_METERING_SAMPLES, total);
    int stride = max(1, total / sampleCount);

    if (MeteringMode == 1)
    {
    float sumLog = 0.0;
    for (int i = 0; i < sampleCount; ++i)
    {
        int idx = i * stride;
        int x = idx % w;
        int y = idx / w;
        float lum = SafeLum(FetchRgbAt(ivec2(x, y), mip));
        sumLog += log(max(lum, 1e-6));
    }
    return exp(sumLog / float(max(sampleCount, 1)));
    }

    if (MeteringMode == 2)
    {
    float sum = 0.0;
    float weightSum = 0.0;
    float invW = 1.0 / float(w);
    float invH = 1.0 / float(h);
    float strength = clamp(CenterWeightStrength, 0.0, 1.0);
    float power = max(CenterWeightPower, 0.1);

    for (int i = 0; i < sampleCount; ++i)
    {
        int idx = i * stride;
        int x = idx % w;
        int y = idx / w;
        float lum = SafeLum(FetchRgbAt(ivec2(x, y), mip));

        float u = (float(x) + 0.5) * invW;
        float v = (float(y) + 0.5) * invH;
        float dx = u - 0.5;
        float dy = v - 0.5;
        float r = sqrt(dx * dx + dy * dy) / 0.70710678;
        float center = pow(clamp(1.0 - r, 0.0, 1.0), power);
        float wgt = mix(1.0, center, strength);

        sum += lum * wgt;
        weightSum += wgt;
    }

    return sum / max(weightSum, 1e-6);
    }

    float lums[MAX_METERING_SAMPLES];
    for (int i = 0; i < sampleCount; ++i)
    {
    int idx = i * stride;
    int x = idx % w;
    int y = idx / w;
    lums[i] = SafeLum(FetchRgbAt(ivec2(x, y), mip));
    }

    for (int i = 1; i < sampleCount; ++i)
    {
    float key = lums[i];
    int j = i - 1;
    while (j >= 0 && lums[j] > key)
    {
        lums[j + 1] = lums[j];
        j--;
    }
    lums[j + 1] = key;
    }

    float drop = clamp(IgnoreTopPercent, 0.0, 0.5);
    int keep = int(floor((1.0 - drop) * float(sampleCount)));
    keep = clamp(keep, 1, sampleCount);

    float sum = 0.0;
    for (int i = 0; i < keep; ++i)
    sum += lums[i];
    return sum / float(keep);
}

void main()
{
    float lumDot = ComputeMeteredLuminance();

    // Clamp extremely bright outliers so they can't force exposure to MinExposure.
    float denom = max((MinExposure / max(ExposureBase, 1e-6)) - AutoExposureBias, 1e-6);
    float maxLumForMinExposure = (ExposureDividend * max(AutoExposureScale, 0.0)) / denom;
    lumDot = min(lumDot, maxLumForMinExposure);

    float current = imageLoad(ExposureOut, ivec2(0, 0)).r;
    bool currentValid = !(isnan(current) || isinf(current)) && current >= MinExposure && current <= MaxExposure;
    float stableCurrent = currentValid ? current : clamp(FallbackExposure, MinExposure, MaxExposure);

    if (!(lumDot > 0.0) || isnan(lumDot) || isinf(lumDot))
    {
    imageStore(ExposureOut, ivec2(0, 0), vec4(stableCurrent, 0.0, 0.0, 0.0));
    return;
    }

    float target = ExposureDividend / lumDot;
    target = AutoExposureBias + AutoExposureScale * target;
    target *= ExposureBase;
    target = clamp(target, MinExposure, MaxExposure);

    if (isnan(target) || isinf(target))
    {
    imageStore(ExposureOut, ivec2(0, 0), vec4(stableCurrent, 0.0, 0.0, 0.0));
    return;
    }

    float outExposure = mix(stableCurrent, target, clamp(ExposureTransitionSpeed, 0.0, 1.0));

    imageStore(ExposureOut, ivec2(0, 0), vec4(outExposure, 0.0, 0.0, 0.0));
}
";

    private void EnsureLuminanceComputeResources()
    {
        if (_luminanceComputeInitialized)
            return;

        try
        {
            var shader = new XRShader(EShaderType.Compute, LuminanceComputeShaderSource);
            _luminanceComputeProgram = new XRRenderProgram(true, false, shader);

            // Create result buffer (single vec4)
            _luminanceResultBuffer = Api.GenBuffer();
            _luminanceResultBufferSize = 16; // sizeof(vec4)
            Api.BindBuffer(GLEnum.ShaderStorageBuffer, _luminanceResultBuffer);
            var nullPtr = IntPtr.Zero;
            Api.BufferData(GLEnum.ShaderStorageBuffer, _luminanceResultBufferSize, in nullPtr, GLEnum.DynamicRead);
            Api.BindBuffer(GLEnum.ShaderStorageBuffer, 0);

            _luminanceComputeInitialized = true;
        }
        catch (Exception ex)
        {
            Debug.OpenGLWarning($"Failed to initialize luminance compute shader: {ex.Message}");
            _luminanceComputeInitialized = false;
        }
    }

    private void EnsureAutoExposureComputeResources()
    {
        if (_autoExposureComputeInitialized)
            return;

        try
        {
            var shader2D = new XRShader(EShaderType.Compute, AutoExposureComputeShaderSource2D);
            _autoExposureComputeProgram2D = new XRRenderProgram(true, false, shader2D);

            var shader2DArray = new XRShader(EShaderType.Compute, AutoExposureComputeShaderSource2DArray);
            _autoExposureComputeProgram2DArray = new XRRenderProgram(true, false, shader2DArray);

            _autoExposureComputeInitialized = true;
        }
        catch (Exception ex)
        {
            Debug.OpenGLWarning($"Failed to initialize auto exposure compute shaders: {ex.Message}");
            _autoExposureComputeInitialized = false;
        }
    }

}
