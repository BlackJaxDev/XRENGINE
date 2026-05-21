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
    private enum FrontLuminanceReadbackMode
    {
        Mipmap,
        Compute
    }

    private sealed class PendingFrontLuminanceReadback
    {
        public required Action<bool, float> Callback { get; init; }
        public required FrontLuminanceReadbackMode Mode { get; init; }
        public required Vector3 LuminanceWeights { get; init; }
        public required IntPtr Sync { get; init; }
        public required long StartedTicks { get; init; }
        public required long LastPollTicks { get; set; }
    }

    private const double FrontLuminanceReadbackMinIntervalMs = 66.0;
    private const double FrontLuminanceReadbackPollIntervalMs = 33.0;
    private const double FrontLuminanceReadbackTimeoutMs = 250.0;

    private PendingFrontLuminanceReadback? _pendingFrontLuminanceReadback;
    private long _lastFrontLuminanceRequestTicks;
    private bool _hasFrontLuminanceSample;
    private float _lastFrontLuminanceSample;

    private static long FrontLuminanceMillisecondsToTicks(double milliseconds)
        => (long)(System.Diagnostics.Stopwatch.Frequency * (milliseconds / 1000.0));

    private void QueueFrontLuminanceCallback(Action<bool, float> callback, bool success, float dot)
        => RuntimeEngine.EnqueueAppThreadTask(() => callback(success, dot), "GLRenderer.FrontLuminance.Callback");

    private void QueueCachedFrontLuminanceCallback(Action<bool, float> callback)
        => QueueFrontLuminanceCallback(callback, _hasFrontLuminanceSample, _hasFrontLuminanceSample ? _lastFrontLuminanceSample : 0.0f);

    private void CompletePendingFrontLuminanceReadback(PendingFrontLuminanceReadback pending, bool success, float dot)
    {
        _pendingFrontLuminanceReadback = null;
        QueueFrontLuminanceCallback(pending.Callback, success, dot);
    }

    private void CancelPendingFrontLuminanceReadback()
    {
        if (_pendingFrontLuminanceReadback is not { } pending)
            return;

        if (!ShouldOrphanGLHandlesForShutdown)
            Api.DeleteSync(pending.Sync);
        _pendingFrontLuminanceReadback = null;
    }

    private unsafe bool TryServicePendingFrontLuminanceReadback(long nowTicks)
    {
        if (_pendingFrontLuminanceReadback is not { } pending)
            return false;

        long timeoutTicks = FrontLuminanceMillisecondsToTicks(FrontLuminanceReadbackTimeoutMs);
        if (nowTicks - pending.StartedTicks >= timeoutTicks)
        {
            Api.DeleteSync(pending.Sync);
            CompletePendingFrontLuminanceReadback(pending, _hasFrontLuminanceSample, _hasFrontLuminanceSample ? _lastFrontLuminanceSample : 0.0f);
            return false;
        }

        long pollIntervalTicks = FrontLuminanceMillisecondsToTicks(FrontLuminanceReadbackPollIntervalMs);
        if (nowTicks - pending.LastPollTicks < pollIntervalTicks)
            return true;

        pending.LastPollTicks = nowTicks;

        switch (pending.Mode)
        {
            case FrontLuminanceReadbackMode.Mipmap:
                {
                    if (!GetData(_luminanceFrontPboSize, _rgbDataForAsync(ref _asyncBuffer), pending.Sync, _luminanceFrontPbo))
                        return true;

                    Api.DeleteSync(pending.Sync);

                    float r = _asyncBuffer[0] / 255.0f;
                    float g = _asyncBuffer[1] / 255.0f;
                    float b = _asyncBuffer[2] / 255.0f;
                    if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b))
                    {
                        CompletePendingFrontLuminanceReadback(pending, _hasFrontLuminanceSample, _hasFrontLuminanceSample ? _lastFrontLuminanceSample : 0.0f);
                        return false;
                    }

                    _lastFrontLuminanceSample = new Vector3(r, g, b).Dot(pending.LuminanceWeights);
                    _hasFrontLuminanceSample = true;
                    CompletePendingFrontLuminanceReadback(pending, true, _lastFrontLuminanceSample);
                    return false;
                }

            case FrontLuminanceReadbackMode.Compute:
                {
                    var waitResult = Api.ClientWaitSync(pending.Sync, 0u, 0u);
                    if (!(waitResult == GLEnum.AlreadySignaled || waitResult == GLEnum.ConditionSatisfied))
                        return true;

                    Api.DeleteSync(pending.Sync);

                    Vector4 average;
                    Api.BindBuffer(GLEnum.ShaderStorageBuffer, _luminanceResultBuffer);
                    Api.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, 16, &average);
                    Api.BindBuffer(GLEnum.ShaderStorageBuffer, 0);

                    if (float.IsNaN(average.X) || float.IsNaN(average.Y) || float.IsNaN(average.Z))
                    {
                        CompletePendingFrontLuminanceReadback(pending, _hasFrontLuminanceSample, _hasFrontLuminanceSample ? _lastFrontLuminanceSample : 0.0f);
                        return false;
                    }

                    _lastFrontLuminanceSample = new Vector3(average.X, average.Y, average.Z).Dot(pending.LuminanceWeights);
                    _hasFrontLuminanceSample = true;
                    CompletePendingFrontLuminanceReadback(pending, true, _lastFrontLuminanceSample);
                    return false;
                }

            default:
                return false;
        }
    }

    private bool PollPendingFrontLuminanceReadback()
    {
        if (_pendingFrontLuminanceReadback is null)
            return true;

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        return !TryServicePendingFrontLuminanceReadback(nowTicks);
    }

    public override unsafe void CalcDotLuminanceAsync(XRTexture2DArray texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.CalcDotLuminanceAsync");

        if (IsGpuZeroReadbackActive())
        {
            callback(false, 0.0f);
            return;
        }

        var glTex = GenericToAPI<GLTexture2DArray>(texture);
        if (glTex is null)
        {
            callback(false, 0.0f);
            return;
        }

        if (genMipmapsNow)
            glTex.GenerateMipmaps();

        int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);
        int layerCount = (int)texture.Depth;
        if (layerCount <= 0)
        {
            callback(false, 0.0f);
            return;
        }

        uint byteSize = (uint)(sizeof(Vector4) * layerCount);
        uint pbo = Api.GenBuffer();
        Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
        Api.BufferData(GLEnum.PixelPackBuffer, byteSize, null, GLEnum.StreamRead);

        Api.GetTextureSubImage(
            glTex.BindingId,
            mipLevel,
            0, 0, 0,
            1, 1, (uint)layerCount,
            GLObjectBase.ToGLEnum(EPixelFormat.Rgba),
            GLObjectBase.ToGLEnum(EPixelType.Float),
            byteSize,
            (void*)IntPtr.Zero);

        IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

        bool FenceCheck()
        {
            if (!GetData(byteSize, _rgbDataForAsync(ref _asyncBuffer), sync, pbo))
                return false;

            Api.DeleteSync(sync);
            Api.DeleteBuffer(pbo);

            Span<Vector4> samples = MemoryMarshal.Cast<byte, Vector4>(_asyncBuffer.AsSpan(0, (int)byteSize));
            Vector3 accum = Vector3.Zero;
            for (int i = 0; i < layerCount; i++)
            {
                Vector4 s = samples[i];
                if (float.IsNaN(s.X) || float.IsNaN(s.Y) || float.IsNaN(s.Z))
                {
                    callback(false, 0.0f);
                    return true;
                }
                accum += s.XYZ();
            }

            Vector3 average = accum / layerCount;
            callback(true, average.Dot(luminance));
            return true;
        }

        RuntimeEngine.AddMainThreadCoroutine(FenceCheck);
    }

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

    float outExposure = !currentValid
    ? target
    : mix(current, target, clamp(ExposureTransitionSpeed, 0.0, 1.0));

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

    float outExposure = !currentValid
    ? target
    : mix(current, target, clamp(ExposureTransitionSpeed, 0.0, 1.0));

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

    public override bool SupportsGpuAutoExposure
    {
        get
        {
            if (_supportsGpuAutoExposure == false)
                return false;

            _supportsGpuAutoExposure ??= ComputeSupportsGpuAutoExposure();
            if (_supportsGpuAutoExposure != true)
                return false;

            // Lazily compile/initialize once we know the context should support it.
            EnsureAutoExposureComputeResources();

            if (!_autoExposureComputeInitialized)
                _supportsGpuAutoExposure = false;

            return _autoExposureComputeInitialized;
        }
    }

    private bool ComputeSupportsGpuAutoExposure()
    {
        try
        {
            // Compute shaders are core in GL 4.3+. Image load/store is core in GL 4.2+.
            int major = Api.GetInteger(GLEnum.MajorVersion);
            int minor = Api.GetInteger(GLEnum.MinorVersion);

            bool hasCompute = (major > 4) || (major == 4 && minor >= 3) || IsExtensionSupported("GL_ARB_compute_shader");
            bool hasImageLoadStore = (major > 4) || (major == 4 && minor >= 2) || IsExtensionSupported("GL_ARB_shader_image_load_store");

            return hasCompute && hasImageLoadStore;
        }
        catch
        {
            return false;
        }
    }

    public override bool UpdateAutoExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex, ColorGradingSettings settings, float deltaTime, bool generateMipmapsNow)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.UpdateAutoExposureGpu");

        EnsureAutoExposureComputeResources();
        if (!_autoExposureComputeInitialized)
        {
            // Prevent repeated attempts if compilation failed (ensures CPU fallback is used).
            _supportsGpuAutoExposure = false;
            return false;
        }

        var glExposure = GetOrCreateAPIRenderObject(exposureTex, generateNow: true) as GLTexture2D;
        if (glExposure is null)
        {
            string exposureTextureName = exposureTex.Name ?? "<unnamed>";

            // An invalid image binding causes expensive GL debug-driver error handling
            // and repeated stalls. Drop back to the CPU exposure path for this session.
            Debug.OpenGLWarningEvery(
                $"AutoExposure.InvalidExposure.{exposureTextureName}",
                TimeSpan.FromSeconds(1),
                "[AutoExposure] Exposure texture is not ready for image load/store. Texture='{0}', BindingId={1}.",
                exposureTextureName,
                glExposure?.BindingId ?? 0);
            return false;
        }

        // Framebuffer-only textures may still only be a generated GL name here.
        // Bind once to force the driver to materialize the texture object and allocate
        // immutable storage before we use it as an image.
        IGLTexture? previousBoundTexture = BoundTexture;
        glExposure.Bind();
        if (previousBoundTexture is null || ReferenceEquals(previousBoundTexture, glExposure))
            glExposure.Unbind();
        else
            previousBoundTexture.Bind();

        uint exposureBindingId = glExposure.BindingId;
        if (exposureBindingId == GLObjectBase.InvalidBindingId || !Api.IsTexture(exposureBindingId))
        {
            string exposureTextureName = exposureTex.Name ?? "<unnamed>";

            Debug.OpenGLWarningEvery(
                $"AutoExposure.InvalidExposure.{exposureTextureName}",
                TimeSpan.FromSeconds(1),
                "[AutoExposure] Exposure texture is not ready for image load/store. Texture='{0}', BindingId={1}.",
                exposureTextureName,
                exposureBindingId);
            return false;
        }

        int smallestMip;
        GLRenderProgram? glProgram;

        uint bindTarget;
        uint sourceBindingId;
        int layerCount = 1;

        if (sourceTex is XRTexture2D source2D)
        {
            var glSource = GetOrCreateAPIRenderObject(source2D, generateNow: true) as GLTexture2D;
            if (glSource is null || !glSource.TryGetBindingId(out sourceBindingId) || !Api.IsTexture(sourceBindingId))
            {
                _supportsGpuAutoExposure = false;
                Debug.OpenGLWarning($"[AutoExposure] Disabling GPU auto exposure because the source texture is invalid. Texture='{sourceTex.Name}', BindingId={glSource?.BindingId ?? 0}.");
                return false;
            }

            if (generateMipmapsNow)
                glSource.GenerateMipmaps();

            smallestMip = XRTexture.GetSmallestMipmapLevel(source2D.Width, source2D.Height, source2D.SmallestAllowedMipmapLevel);
            glProgram = GenericToAPI<GLRenderProgram>(_autoExposureComputeProgram2D);
            bindTarget = (uint)TextureTarget.Texture2D;
        }
        else if (sourceTex is XRTexture2DArray source2DArray)
        {
            var glSource = GetOrCreateAPIRenderObject(source2DArray, generateNow: true) as GLTexture2DArray;
            if (glSource is null || !glSource.TryGetBindingId(out sourceBindingId) || !Api.IsTexture(sourceBindingId))
            {
                _supportsGpuAutoExposure = false;
                Debug.OpenGLWarning($"[AutoExposure] Disabling GPU auto exposure because the array source texture is invalid. Texture='{sourceTex.Name}', BindingId={glSource?.BindingId ?? 0}.");
                return false;
            }

            if (generateMipmapsNow)
                glSource.GenerateMipmaps();

            smallestMip = XRTexture.GetSmallestMipmapLevel(source2DArray.Width, source2DArray.Height, source2DArray.SmallestAllowedMipmapLevel);
            layerCount = (int)source2DArray.Depth;
            glProgram = GenericToAPI<GLRenderProgram>(_autoExposureComputeProgram2DArray);
            bindTarget = (uint)TextureTarget.Texture2DArray;
        }
        else
        {
            return false;
        }

        if (glProgram is null)
        {
            Debug.OpenGLWarningEvery(
                $"AutoExposure.NullProgram.{sourceTex.GetType().Name}",
                TimeSpan.FromSeconds(1),
                "[AutoExposure] Failed to resolve GL compute program for source texture type '{0}'.",
                sourceTex.GetType().Name);
            return false;
        }

        // The compute program may not be linked yet because linkNow fires before
        // the GL wrapper exists (the event has no subscribers during the constructor).
        // Attempt a deferred link here, mirroring what UseRequested does.
        if (!glProgram.IsLinked && !glProgram.Link())
        {
            Debug.OpenGLWarningEvery(
                $"AutoExposure.ProgramNotReady.{glProgram.Hash}",
                TimeSpan.FromSeconds(1),
                "[AutoExposure] Compute program hash {0} is not ready yet. LinkReady={1}, IsLinked={2}.",
                glProgram.Hash,
                glProgram.LinkReady,
                glProgram.IsLinked);
            return false;
        }

        Api.UseProgram(glProgram.BindingId);

        int meteringMip = smallestMip;
        if (settings.AutoExposureMetering != ColorGradingSettings.AutoExposureMeteringMode.Average)
        {
            int targetSize = Math.Clamp(settings.AutoExposureMeteringTargetSize, 1, 64);
            uint pow2 = 1u << BitOperations.Log2((uint)targetSize);
            int offset = BitOperations.Log2(pow2);
            meteringMip = Math.Clamp(smallestMip - offset, 0, smallestMip);
        }

        glProgram.Uniform("SmallestMip", smallestMip);
        glProgram.Uniform("LuminanceWeights", settings.AutoExposureLuminanceWeights);
        glProgram.Uniform("AutoExposureBias", settings.AutoExposureBias);
        glProgram.Uniform("AutoExposureScale", settings.AutoExposureScale);
        glProgram.Uniform("ExposureDividend", settings.ExposureDividend);
        settings.GetResolvedExposureBounds(out float minExposure, out float maxExposure);

        glProgram.Uniform("MinExposure", minExposure);
        glProgram.Uniform("MaxExposure", maxExposure);
        glProgram.Uniform("ExposureBase", settings.ExposureMode == ColorGradingSettings.ExposureControlMode.Physical
            ? settings.ComputePhysicalExposureMultiplier()
            : 1.0f);
        float fallbackExposure = settings.ExposureMode == ColorGradingSettings.ExposureControlMode.Physical
            ? settings.ComputePhysicalExposureMultiplier()
            : settings.Exposure;
        glProgram.Uniform("FallbackExposure", Math.Clamp(fallbackExposure, minExposure, maxExposure));

        glProgram.Uniform("MeteringMode", (int)settings.AutoExposureMetering);
        glProgram.Uniform("MeteringMip", meteringMip);
        glProgram.Uniform("IgnoreTopPercent", settings.AutoExposureIgnoreTopPercent);
        glProgram.Uniform("CenterWeightStrength", settings.AutoExposureCenterWeightStrength);
        glProgram.Uniform("CenterWeightPower", settings.AutoExposureCenterWeightPower);

        // Calculate time-based lerp factor
        // alpha = 1 - exp(-speed * dt)
        float alpha = 1.0f - MathF.Exp(-settings.ExposureTransitionSpeed * deltaTime);
        glProgram.Uniform("ExposureTransitionSpeed", alpha);

        if (sourceTex is XRTexture2DArray)
            glProgram.Uniform("LayerCount", layerCount);

        SetActiveTextureUnit(0);
        Api.BindTexture((TextureTarget)bindTarget, sourceBindingId);
        glProgram.Uniform("SourceTex", 0);

        // Bind exposure texture as an image for read/write
        Api.BindImageTexture(0, exposureBindingId, 0, false, 0, BufferAccessARB.ReadWrite, InternalFormat.R32f);

        Api.DispatchCompute(1, 1, 1);
        Api.MemoryBarrier((uint)(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit));

        // Ensure that the compute shader write is visible to subsequent reads (by the fragment shader or the next compute dispatch)
        Api.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit);
        return true;
    }

    public override unsafe void CalcDotLuminanceAsync(XRTexture2D texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.CalcDotLuminanceAsync");

        if (IsGpuZeroReadbackActive())
        {
            callback(false, 0.0f);
            return;
        }

        var glTex = GenericToAPI<GLTexture2D>(texture);
        if (glTex is null)
        {
            callback(false, 0.0f);
            return;
        }

        if (genMipmapsNow)
            glTex.GenerateMipmaps();

        int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);

        uint byteSize = (uint)sizeof(Vector4);
        uint pbo = Api.GenBuffer();
        Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
        Api.BufferData(GLEnum.PixelPackBuffer, byteSize, null, GLEnum.StreamRead);

        Api.GetTextureImage(
            glTex.BindingId,
            mipLevel,
            GLObjectBase.ToGLEnum(EPixelFormat.Rgba),
            GLObjectBase.ToGLEnum(EPixelType.Float),
            byteSize,
            (void*)IntPtr.Zero);

        IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

        bool FenceCheck()
        {
            if (!GetData(byteSize, _rgbDataForAsync(ref _asyncBuffer), sync, pbo))
                return false;

            Api.DeleteSync(sync);
            Api.DeleteBuffer(pbo);

            Vector3 rgb;
            unsafe
            {
                fixed (byte* ptr = _asyncBuffer)
                {
                    float* fptr = (float*)ptr;
                    rgb = new(fptr[0], fptr[1], fptr[2]);
                }
            }

            if (float.IsNaN(rgb.X) || float.IsNaN(rgb.Y) || float.IsNaN(rgb.Z))
            {
                callback(false, 0.0f);
                return true;
            }

            callback(true, rgb.Dot(luminance));
            return true;
        }

        RuntimeEngine.AddMainThreadCoroutine(FenceCheck);
    }

    public override unsafe bool CalcDotLuminance(XRTexture2DArray texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow = true)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.CalcDotLuminance");

        dotLuminance = 1.0f;
        if (IsGpuZeroReadbackActive())
            return false;

        var glTex = GenericToAPI<GLTexture2DArray>(texture);
        if (glTex is null)
            return false;

        if (genMipmapsNow)
            glTex.GenerateMipmaps();

        int layerCount = (int)texture.Depth;
        if (layerCount <= 0)
            return false;

        Span<Vector4> samples = layerCount <= 8
            ? stackalloc Vector4[layerCount]
            : new Vector4[layerCount];

        int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);

        fixed (Vector4* ptr = samples)
        {
            uint byteSize = (uint)(sizeof(Vector4) * layerCount);
            Api.GetTextureImage(
                glTex.BindingId,
                mipLevel,
                GLObjectBase.ToGLEnum(EPixelFormat.Rgba),
                GLObjectBase.ToGLEnum(EPixelType.Float),
                byteSize,
                ptr);
        }

        Vector3 accum = Vector3.Zero;
        for (int i = 0; i < samples.Length; i++)
        {
            Vector4 sample = samples[i];
            if (float.IsNaN(sample.X) || float.IsNaN(sample.Y) || float.IsNaN(sample.Z))
                return false;

            accum += sample.XYZ();
        }

        Vector3 average = accum / layerCount;
        dotLuminance = average.Dot(luminance);
        return true;
    }
    public override unsafe bool CalcDotLuminance(XRTexture2D texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow = true)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.CalcDotLuminance");

        dotLuminance = 1.0f;
        if (IsGpuZeroReadbackActive())
            return false;

        var glTex = GenericToAPI<GLTexture2D>(texture);
        if (glTex is null)
            return false;

        //Calculate average color value using 1x1 mipmap of scene
        if (genMipmapsNow)
            glTex.GenerateMipmaps();

        int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);

        //Get the average color from the scene texture
        Vector4 rgb = Vector4.Zero;
        void* addr = &rgb;
        Api.GetTextureImage(glTex.BindingId, mipLevel, GLObjectBase.ToGLEnum(EPixelFormat.Rgba), GLObjectBase.ToGLEnum(EPixelType.Float), (uint)sizeof(Vector4), addr);

        if (float.IsNaN(rgb.X) ||
            float.IsNaN(rgb.Y) ||
            float.IsNaN(rgb.Z))
            return false;

        //Calculate luminance factor off of the average color
        dotLuminance = rgb.XYZ().Dot(luminance);
        return true;
    }

    /// <inheritdoc/>
    public override unsafe float ReadTextureCenterRedMip0(XRTexture2D texture)
    {
        if (IsGpuZeroReadbackActive())
            return 0.0f;

        var glTex = GenericToAPI<GLTexture2D>(texture);
        if (glTex is null || !glTex.IsGenerated)
            return 0.0f;

        uint w = texture.Width;
        uint h = texture.Height;
        if (w == 0 || h == 0)
            return 0.0f;

        // Read a single center pixel at mip 0 via glGetTextureSubImage (DSA, no binding changes).
        int cx = (int)(w / 2);
        int cy = (int)(h / 2);
        float pixel = 0.0f;
        Api.GetTextureSubImage(
            glTex.BindingId,
            0,              // mip level 0
            cx, cy, 0,      // offset
            1u, 1u, 1u,     // size: 1x1x1
            GLObjectBase.ToGLEnum(EPixelFormat.Red),
            GLObjectBase.ToGLEnum(EPixelType.Float),
            (uint)sizeof(float),
            &pixel);

        return float.IsNaN(pixel) ? 0.0f : pixel;
    }

    public override unsafe void CalcDotLuminanceFrontAsync(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.CalcDotLuminanceFrontAsync");

        if (IsGpuZeroReadbackActive())
        {
            QueueCachedFrontLuminanceCallback(callback);
            return;
        }

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        TryServicePendingFrontLuminanceReadback(nowTicks);
        if (_pendingFrontLuminanceReadback is not null)
        {
            QueueCachedFrontLuminanceCallback(callback);
            return;
        }

        long minIntervalTicks = FrontLuminanceMillisecondsToTicks(FrontLuminanceReadbackMinIntervalMs);
        if (_hasFrontLuminanceSample && nowTicks - _lastFrontLuminanceRequestTicks < minIntervalTicks)
        {
            QueueCachedFrontLuminanceCallback(callback);
            return;
        }

        uint w = (uint)region.Width;
        uint h = (uint)region.Height;
        if (w == 0 || h == 0)
        {
            QueueFrontLuminanceCallback(callback, false, 0.0f);
            return;
        }

        // Copy the requested front buffer region into a cached FBO-backed texture, generate mipmaps, then read the 1x1 mip via ReadPixels.
        Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        Api.ReadBuffer(ReadBufferMode.Front);

        // Check if we need to reallocate the cached texture/FBO (dimensions changed or not yet allocated)
        int mipLevels = 1 + (int)MathF.Floor(MathF.Log2(MathF.Max(w, h)));
        if (mipLevels < 1)
            mipLevels = 1;

        if (_luminanceFrontTex == 0 || _luminanceFrontTexWidth != w || _luminanceFrontTexHeight != h)
        {
            // Clean up old resources if they exist
            if (_luminanceFrontTex != 0)
                Api.DeleteTexture(_luminanceFrontTex);
            if (_luminanceFrontFbo != 0)
                Api.DeleteFramebuffer(_luminanceFrontFbo);
            if (_luminanceFrontPbo != 0)
                Api.DeleteBuffer(_luminanceFrontPbo);

            // Create new texture with immutable storage
            _luminanceFrontTex = Api.GenTexture();
            Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
            Api.TexStorage2D(TextureTarget.Texture2D, (uint)mipLevels, GLEnum.Rgba8, w, h);

            // Create FBO and attach texture
            _luminanceFrontFbo = Api.GenFramebuffer();
            Api.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _luminanceFrontFbo);
            Api.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, 0);

            // Create cached PBO for readback (4 bytes for RGBA8)
            _luminanceFrontPbo = Api.GenBuffer();
            _luminanceFrontPboSize = 4;
            Api.BindBuffer(GLEnum.PixelPackBuffer, _luminanceFrontPbo);
            var nullPtr = IntPtr.Zero;
            Api.BufferData(GLEnum.PixelPackBuffer, _luminanceFrontPboSize, in nullPtr, GLEnum.StreamRead);
            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

            _luminanceFrontTexWidth = w;
            _luminanceFrontTexHeight = h;
            _luminanceFrontMipLevels = mipLevels;
        }
        else
        {
            Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
            Api.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _luminanceFrontFbo);
            // Re-attach mip 0 for the blit target
            Api.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, 0);
        }

        Api.BlitFramebuffer(
            region.X, region.Y, region.X + (int)w, region.Y + (int)h,
            0, 0, (int)w, (int)h,
            ClearBufferMask.ColorBufferBit,
            GLEnum.Linear);

        Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
        Api.GenerateMipmap(TextureTarget.Texture2D);

        int mipLevel = XRTexture.GetSmallestMipmapLevel(w, h);

        // Re-attach the smallest mip for readback.
        Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _luminanceFrontFbo);
        Api.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, mipLevel);
        Api.ReadBuffer(ReadBufferMode.ColorAttachment0);

        // Use cached PBO for async readback
        uint pbo = _luminanceFrontPbo;
        uint byteSize = _luminanceFrontPboSize;
        Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);

        Api.ReadPixels(0, 0, 1, 1, GLObjectBase.ToGLEnum(EPixelFormat.Rgba), GLObjectBase.ToGLEnum(EPixelType.UnsignedByte), (void*)IntPtr.Zero);

        IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

        _lastFrontLuminanceRequestTicks = nowTicks;
        _pendingFrontLuminanceReadback = new PendingFrontLuminanceReadback
        {
            Callback = callback,
            Mode = FrontLuminanceReadbackMode.Mipmap,
            LuminanceWeights = luminance,
            Sync = sync,
            StartedTicks = nowTicks,
            LastPollTicks = nowTicks
        };
        RuntimeEngine.AddMainThreadCoroutine(PollPendingFrontLuminanceReadback, "GLRenderer.FrontLuminanceReadback");
    }

    /// <summary>
    /// Calculates average luminance using a compute shader for parallel reduction.
    /// This is an alternative to the mipmap-based approach that can be more efficient for large textures.
    /// </summary>
    public unsafe override void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.CalcDotLuminanceFrontAsyncCompute");

        if (IsGpuZeroReadbackActive())
        {
            QueueCachedFrontLuminanceCallback(callback);
            return;
        }

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        TryServicePendingFrontLuminanceReadback(nowTicks);
        if (_pendingFrontLuminanceReadback is not null)
        {
            QueueCachedFrontLuminanceCallback(callback);
            return;
        }

        long minIntervalTicks = FrontLuminanceMillisecondsToTicks(FrontLuminanceReadbackMinIntervalMs);
        if (_hasFrontLuminanceSample && nowTicks - _lastFrontLuminanceRequestTicks < minIntervalTicks)
        {
            QueueCachedFrontLuminanceCallback(callback);
            return;
        }

        uint w = (uint)region.Width;
        uint h = (uint)region.Height;
        if (w == 0 || h == 0)
        {
            QueueFrontLuminanceCallback(callback, false, 0.0f);
            return;
        }

        EnsureLuminanceComputeResources();
        if (!_luminanceComputeInitialized || _luminanceComputeProgram is null)
        {
            // Fall back to mipmap method
            CalcDotLuminanceFrontAsync(region, withTransparency, luminance, callback);
            return;
        }

        // First, blit front buffer to texture (reuse existing cached texture)
        Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        Api.ReadBuffer(ReadBufferMode.Front);

        int mipLevels = 1; // No mipmaps needed for compute path
        if (_luminanceFrontTex == 0 || _luminanceFrontTexWidth != w || _luminanceFrontTexHeight != h)
        {
            if (_luminanceFrontTex != 0)
                Api.DeleteTexture(_luminanceFrontTex);
            if (_luminanceFrontFbo != 0)
                Api.DeleteFramebuffer(_luminanceFrontFbo);

            _luminanceFrontTex = Api.GenTexture();
            Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
            Api.TexStorage2D(TextureTarget.Texture2D, (uint)mipLevels, GLEnum.Rgba8, w, h);

            _luminanceFrontFbo = Api.GenFramebuffer();
            Api.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _luminanceFrontFbo);
            Api.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, 0);

            _luminanceFrontTexWidth = w;
            _luminanceFrontTexHeight = h;
            _luminanceFrontMipLevels = mipLevels;
        }
        else
        {
            Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
            Api.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _luminanceFrontFbo);
            Api.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, 0);
        }

        Api.BlitFramebuffer(
            region.X, region.Y, region.X + (int)w, region.Y + (int)h,
            0, 0, (int)w, (int)h,
            ClearBufferMask.ColorBufferBit,
            GLEnum.Linear);

        // Clear result buffer
        Vector4 zero = Vector4.Zero;
        Api.BindBuffer(GLEnum.ShaderStorageBuffer, _luminanceResultBuffer);
        Api.BufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, 16, &zero);

        // Bind resources and dispatch compute
        var glProgram = GenericToAPI<GLRenderProgram>(_luminanceComputeProgram);
        if (glProgram is null || !glProgram.IsLinked)
        {
            callback(false, 0.0f);
            return;
        }

        try
        {
            Api.UseProgram(glProgram.BindingId);
            glProgram.Uniform("textureSize", new Data.Vectors.IVector2((int)w, (int)h));
            glProgram.Uniform("luminanceWeights", luminance);

            SetActiveTextureUnit(0);
            Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
            glProgram.Uniform("inputTexture", 0);

            Api.BindBufferBase(GLEnum.ShaderStorageBuffer, 0, _luminanceResultBuffer);

            uint groupsX = (w + 15) / 16;
            uint groupsY = (h + 15) / 16;
            Api.DispatchCompute(groupsX, groupsY, 1);

            Api.MemoryBarrier((uint)MemoryBarrierMask.ShaderStorageBarrierBit);
        }
        finally
        {
            Api.BindBufferBase(GLEnum.ShaderStorageBuffer, 0, 0);
            Api.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
        }

        // Async readback from result buffer
        IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);

        _lastFrontLuminanceRequestTicks = nowTicks;
        _pendingFrontLuminanceReadback = new PendingFrontLuminanceReadback
        {
            Callback = callback,
            Mode = FrontLuminanceReadbackMode.Compute,
            LuminanceWeights = luminance,
            Sync = sync,
            StartedTicks = nowTicks,
            LastPollTicks = nowTicks
        };
        RuntimeEngine.AddMainThreadCoroutine(PollPendingFrontLuminanceReadback, "GLRenderer.FrontLuminanceReadback");
    }

    /// <summary>
    /// Checks if GL_ARB_texture_filter_minmax extension is available.
    /// This extension provides hardware-accelerated min/max/average filtering.
    /// </summary>
    public bool SupportsTextureFilterMinmax => HasTextureFilterMinmax;
}
