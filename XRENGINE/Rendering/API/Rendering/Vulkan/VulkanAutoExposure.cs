using System;
using System.Numerics;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private bool? _supportsGpuAutoExposure;
    private bool _autoExposureComputeInitialized;
    private XRRenderProgram? _autoExposureComputeProgram2D;
    private XRRenderProgram? _autoExposureComputeProgram2DArray;
    private bool _autoExposureTextureInitialized;

    public override bool SupportsGpuAutoExposure
    {
        get
        {
            if (_supportsGpuAutoExposure == false)
                return false;

            _supportsGpuAutoExposure ??= ComputeSupportsGpuAutoExposure();
            if (_supportsGpuAutoExposure != true)
                return false;

            EnsureAutoExposureComputeResources();
            if (!_autoExposureComputeInitialized)
                _supportsGpuAutoExposure = false;

            return _autoExposureComputeInitialized;
        }
    }

    public override void UpdateAutoExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex, ColorGradingSettings settings, float deltaTime, bool generateMipmapsNow)
    {
        EnsureAutoExposureComputeResources();
        if (!_autoExposureComputeInitialized)
        {
            _supportsGpuAutoExposure = false;
            return;
        }

        if (sourceTex is null || exposureTex is null)
            return;

        if (!EnsureExposureStorageUsage(exposureTex))
            return;

        XRRenderProgram? program;
        int smallestMip;
        int layerCount = 1;

        if (sourceTex is XRTexture2D source2D)
        {
            if (generateMipmapsNow)
                source2D.GenerateMipmapsGPU();

            smallestMip = XRTexture.GetSmallestMipmapLevel(source2D.Width, source2D.Height, source2D.SmallestAllowedMipmapLevel);
            program = _autoExposureComputeProgram2D;
        }
        else if (sourceTex is XRTexture2DArray source2DArray)
        {
            if (generateMipmapsNow)
                source2DArray.GenerateMipmapsGPU();

            smallestMip = XRTexture.GetSmallestMipmapLevel(source2DArray.Width, source2DArray.Height, source2DArray.SmallestAllowedMipmapLevel);
            layerCount = (int)Math.Max(source2DArray.Depth, 1u);
            program = _autoExposureComputeProgram2DArray;
        }
        else
        {
            return;
        }

        if (program is null)
            return;

        int meteringMip = smallestMip;
        if (settings.AutoExposureMetering != ColorGradingSettings.AutoExposureMeteringMode.Average)
        {
            int targetSize = Math.Clamp(settings.AutoExposureMeteringTargetSize, 1, 64);
            uint pow2 = 1u << BitOperations.Log2((uint)targetSize);
            int offset = BitOperations.Log2(pow2);
            meteringMip = Math.Clamp(smallestMip - offset, 0, smallestMip);
        }

        float alpha = 1.0f - MathF.Exp(-settings.ExposureTransitionSpeed * deltaTime);

        // Ensure exposure image is in GENERAL for storage write.
        if (GetOrCreateAPIRenderObject(exposureTex, generateNow: true) is VkTexture2D vkExposure)
        {
            if (_autoExposureTextureInitialized)
                vkExposure.TransitionImageLayout(Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal, Silk.NET.Vulkan.ImageLayout.General);
            else
                vkExposure.TransitionImageLayout(Silk.NET.Vulkan.ImageLayout.Undefined, Silk.NET.Vulkan.ImageLayout.General);
        }

        program.Uniform("SmallestMip", smallestMip);
        program.Uniform("LuminanceWeights", settings.AutoExposureLuminanceWeights);
        program.Uniform("AutoExposureBias", settings.AutoExposureBias);
        program.Uniform("AutoExposureScale", settings.AutoExposureScale);
        program.Uniform("ExposureDividend", settings.ExposureDividend);
        program.Uniform("MinExposure", settings.MinExposure);
        program.Uniform("MaxExposure", settings.MaxExposure);
        program.Uniform("ExposureBase", settings.ExposureMode == ColorGradingSettings.ExposureControlMode.Physical
            ? settings.ComputePhysicalExposureMultiplier()
            : 1.0f);

        program.Uniform("MeteringMode", (int)settings.AutoExposureMetering);
        program.Uniform("MeteringMip", meteringMip);
        program.Uniform("IgnoreTopPercent", settings.AutoExposureIgnoreTopPercent);
        program.Uniform("CenterWeightStrength", settings.AutoExposureCenterWeightStrength);
        program.Uniform("CenterWeightPower", settings.AutoExposureCenterWeightPower);
        program.Uniform("ExposureTransitionSpeed", alpha);

        if (sourceTex is XRTexture2DArray)
            program.Uniform("LayerCount", layerCount);

        program.Sampler("SourceTex", sourceTex, 0);
        program.BindImageTexture(
            1,
            exposureTex,
            0,
            false,
            0,
            XRRenderProgram.EImageAccess.ReadWrite,
            XRRenderProgram.EImageFormat.R32F);

        DispatchCompute(program, 1, 1, 1);
        MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.TextureFetch);

        if (GetOrCreateAPIRenderObject(exposureTex, generateNow: true) is VkTexture2D vkExposurePost)
            vkExposurePost.TransitionImageLayout(Silk.NET.Vulkan.ImageLayout.General, Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal);

        _autoExposureTextureInitialized = true;
    }

    private bool ComputeSupportsGpuAutoExposure()
    {
        // Vulkan backend requires a compute-capable queue for this path.
        return FamilyQueueIndices.ComputeFamilyIndex.HasValue;
    }

    private bool EnsureExposureStorageUsage(XRTexture2D exposureTex)
    {
        if (GetOrCreateAPIRenderObject(exposureTex) is not VkTexture2D vkExposure)
            return false;

        if ((vkExposure.Usage & Silk.NET.Vulkan.ImageUsageFlags.StorageBit) != 0)
            return true;

        vkExposure.Usage |= Silk.NET.Vulkan.ImageUsageFlags.StorageBit;

        if (vkExposure.IsGenerated)
        {
            vkExposure.Destroy();
            vkExposure.Generate();
            _autoExposureTextureInitialized = false;
        }

        return true;
    }

    private void EnsureAutoExposureComputeResources()
    {
        if (_autoExposureComputeInitialized)
            return;

        try
        {
            _autoExposureComputeProgram2D = new XRRenderProgram(
                linkNow: true,
                separable: false,
                new XRShader(EShaderType.Compute, AutoExposureComputeShaderSource2D)
                {
                    Name = "VulkanAutoExposure2D.comp"
                });

            _autoExposureComputeProgram2DArray = new XRRenderProgram(
                linkNow: true,
                separable: false,
                new XRShader(EShaderType.Compute, AutoExposureComputeShaderSource2DArray)
                {
                    Name = "VulkanAutoExposure2DArray.comp"
                });

            _autoExposureComputeInitialized = true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"Failed to initialize Vulkan auto exposure compute shaders: {ex.Message}");
            _autoExposureComputeInitialized = false;
            _supportsGpuAutoExposure = false;
        }
    }

    private void DestroyAutoExposureComputeResources()
    {
        _autoExposureComputeProgram2D?.Destroy();
        _autoExposureComputeProgram2DArray?.Destroy();
        _autoExposureComputeProgram2D = null;
        _autoExposureComputeProgram2DArray = null;
        _autoExposureComputeInitialized = false;
        _autoExposureTextureInitialized = false;
    }

    private const string AutoExposureComputeShaderSource2D = @"#version 460
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout(binding = 0) uniform sampler2D SourceTex;
layout(r32f, binding = 1) uniform image2D ExposureOut;

uniform int SmallestMip;
uniform vec3 LuminanceWeights;
uniform float AutoExposureBias;
uniform float AutoExposureScale;
uniform float ExposureDividend;
uniform float MinExposure;
uniform float MaxExposure;
uniform float ExposureTransitionSpeed;
uniform float ExposureBase;

uniform int MeteringMode;
uniform int MeteringMip;
uniform float IgnoreTopPercent;
uniform float CenterWeightStrength;
uniform float CenterWeightPower;

const int MAX_SAMPLES = 256;

float SafeLum(vec3 rgb)
{
    float l = dot(max(rgb, vec3(0.0)), LuminanceWeights);
    return max(l, 1e-6);
}

float ComputeMeteredLuminance()
{
    if (MeteringMode == 0)
    {
        vec3 src = texelFetch(SourceTex, ivec2(0, 0), SmallestMip).rgb;
        return SafeLum(src);
    }

    int mip = clamp(MeteringMip, 0, SmallestMip);
    ivec2 sz = textureSize(SourceTex, mip);
    int w = max(sz.x, 1);
    int h = max(sz.y, 1);

    int sampleCount = min(w * h, MAX_SAMPLES);
    if (sampleCount <= 0)
    {
        vec3 src = texelFetch(SourceTex, ivec2(0, 0), SmallestMip).rgb;
        return SafeLum(src);
    }

    float lums[MAX_SAMPLES];
    int stride = max((w * h) / sampleCount, 1);

    for (int i = 0; i < sampleCount; ++i)
    {
        int idx = i * stride;
        int x = idx % w;
        int y = idx / w;
        vec3 rgb = texelFetch(SourceTex, ivec2(x, y), mip).rgb;
        float lum = SafeLum(rgb);

        if (MeteringMode == 1)
            lum = log2(lum);
        else if (MeteringMode == 2)
        {
            vec2 uv = (vec2(x, y) + vec2(0.5)) / vec2(w, h);
            vec2 d = uv - vec2(0.5);
            float r = length(d) / 0.70710678;
            float weight = mix(1.0, max(0.0, 1.0 - r), clamp(CenterWeightStrength, 0.0, 1.0));
            weight = pow(max(weight, 1e-4), max(CenterWeightPower, 0.01));
            lum *= weight;
        }

        lums[i] = lum;
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

    float avg = sum / float(keep);
    return (MeteringMode == 1) ? exp2(avg) : avg;
}

void main()
{
    float lumDot = ComputeMeteredLuminance();

    float denom = max((MinExposure / max(ExposureBase, 1e-6)) - AutoExposureBias, 1e-6);
    float maxLumForMinExposure = (ExposureDividend * max(AutoExposureScale, 0.0)) / denom;
    lumDot = min(lumDot, maxLumForMinExposure);

    float current = imageLoad(ExposureOut, ivec2(0, 0)).r;
    if (isnan(current) || isinf(current))
        current = 1.0;
    float clampedCurrent = clamp(current, MinExposure, MaxExposure);

    if (!(lumDot > 0.0) || isnan(lumDot) || isinf(lumDot))
    {
        imageStore(ExposureOut, ivec2(0, 0), vec4(clampedCurrent, 0.0, 0.0, 0.0));
        return;
    }

    float target = ExposureDividend / lumDot;
    target = AutoExposureBias + AutoExposureScale * target;
    target *= ExposureBase;
    target = clamp(target, MinExposure, MaxExposure);

    if (isnan(target) || isinf(target))
    {
        imageStore(ExposureOut, ivec2(0, 0), vec4(clampedCurrent, 0.0, 0.0, 0.0));
        return;
    }

    float outExposure = (current < MinExposure || current > MaxExposure)
        ? target
        : mix(current, target, clamp(ExposureTransitionSpeed, 0.0, 1.0));

    imageStore(ExposureOut, ivec2(0, 0), vec4(outExposure, 0.0, 0.0, 0.0));
}
";

    private const string AutoExposureComputeShaderSource2DArray = @"#version 460
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout(binding = 0) uniform sampler2DArray SourceTex;
layout(r32f, binding = 1) uniform image2D ExposureOut;

uniform int SmallestMip;
uniform vec3 LuminanceWeights;
uniform float AutoExposureBias;
uniform float AutoExposureScale;
uniform float ExposureDividend;
uniform float MinExposure;
uniform float MaxExposure;
uniform float ExposureTransitionSpeed;
uniform float ExposureBase;

uniform int MeteringMode;
uniform int MeteringMip;
uniform float IgnoreTopPercent;
uniform float CenterWeightStrength;
uniform float CenterWeightPower;
uniform int LayerCount;

const int MAX_SAMPLES = 256;

float SafeLum(vec3 rgb)
{
    float l = dot(max(rgb, vec3(0.0)), LuminanceWeights);
    return max(l, 1e-6);
}

float ComputeMeteredLuminanceForLayer(int layer)
{
    if (MeteringMode == 0)
    {
        vec3 src = texelFetch(SourceTex, ivec3(0, 0, layer), SmallestMip).rgb;
        return SafeLum(src);
    }

    int mip = clamp(MeteringMip, 0, SmallestMip);
    ivec2 sz = textureSize(SourceTex, mip).xy;
    int w = max(sz.x, 1);
    int h = max(sz.y, 1);

    int sampleCount = min(w * h, MAX_SAMPLES);
    if (sampleCount <= 0)
    {
        vec3 src = texelFetch(SourceTex, ivec3(0, 0, layer), SmallestMip).rgb;
        return SafeLum(src);
    }

    float lums[MAX_SAMPLES];
    int stride = max((w * h) / sampleCount, 1);

    for (int i = 0; i < sampleCount; ++i)
    {
        int idx = i * stride;
        int x = idx % w;
        int y = idx / w;
        vec3 rgb = texelFetch(SourceTex, ivec3(x, y, layer), mip).rgb;
        float lum = SafeLum(rgb);

        if (MeteringMode == 1)
            lum = log2(lum);
        else if (MeteringMode == 2)
        {
            vec2 uv = (vec2(x, y) + vec2(0.5)) / vec2(w, h);
            vec2 d = uv - vec2(0.5);
            float r = length(d) / 0.70710678;
            float weight = mix(1.0, max(0.0, 1.0 - r), clamp(CenterWeightStrength, 0.0, 1.0));
            weight = pow(max(weight, 1e-4), max(CenterWeightPower, 0.01));
            lum *= weight;
        }

        lums[i] = lum;
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

    float avg = sum / float(keep);
    return (MeteringMode == 1) ? exp2(avg) : avg;
}

void main()
{
    int layers = max(LayerCount, 1);
    float lumDot = 0.0;
    for (int layer = 0; layer < layers; ++layer)
        lumDot += ComputeMeteredLuminanceForLayer(layer);
    lumDot /= float(layers);

    float denom = max((MinExposure / max(ExposureBase, 1e-6)) - AutoExposureBias, 1e-6);
    float maxLumForMinExposure = (ExposureDividend * max(AutoExposureScale, 0.0)) / denom;
    lumDot = min(lumDot, maxLumForMinExposure);

    float current = imageLoad(ExposureOut, ivec2(0, 0)).r;
    if (isnan(current) || isinf(current))
        current = 1.0;
    float clampedCurrent = clamp(current, MinExposure, MaxExposure);

    if (!(lumDot > 0.0) || isnan(lumDot) || isinf(lumDot))
    {
        imageStore(ExposureOut, ivec2(0, 0), vec4(clampedCurrent, 0.0, 0.0, 0.0));
        return;
    }

    float target = ExposureDividend / lumDot;
    target = AutoExposureBias + AutoExposureScale * target;
    target *= ExposureBase;
    target = clamp(target, MinExposure, MaxExposure);

    if (isnan(target) || isinf(target))
    {
        imageStore(ExposureOut, ivec2(0, 0), vec4(clampedCurrent, 0.0, 0.0, 0.0));
        return;
    }

    float outExposure = (current < MinExposure || current > MaxExposure)
        ? target
        : mix(current, target, clamp(ExposureTransitionSpeed, 0.0, 1.0));

    imageStore(ExposureOut, ivec2(0, 0), vec4(outExposure, 0.0, 0.0, 0.0));
}
";
}
