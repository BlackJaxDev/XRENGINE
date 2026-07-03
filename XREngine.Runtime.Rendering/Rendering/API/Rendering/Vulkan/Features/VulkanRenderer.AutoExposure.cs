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

    public override bool UpdateAutoExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex, ColorGradingSettings settings, float deltaTime, bool generateMipmapsNow)
    {
        EnsureAutoExposureComputeResources();
        if (!_autoExposureComputeInitialized)
        {
            _supportsGpuAutoExposure = false;
            return false;
        }

        if (sourceTex is null || exposureTex is null)
            return false;

        if (!EnsureExposureStorageUsage(exposureTex))
            return false;

        XRRenderProgram? program;
        int smallestMip;
        int sampledSmallestMip;
        int layerCount = 1;
        bool useMiplessMeteringFallback = false;

        if (sourceTex is XRTexture2D source2D)
        {
            if (generateMipmapsNow)
            {
                bool canGenerateMipmapsOutOfBand = true;
                if (GetOrCreateAPIRenderObject(source2D, generateNow: true) is VkTexture2D vkSource2D && vkSource2D.UsesAllocatorImage)
                    canGenerateMipmapsOutOfBand = false;

                if (canGenerateMipmapsOutOfBand)
                {
                    source2D.GenerateMipmapsGPU();
                }
                else
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.AutoExposure.SkipPlannerMipmaps2D",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Skipping out-of-band mipmap generation for planner-backed source texture '{0}' to avoid layout races with render-graph barriers.",
                        source2D.Name ?? "<unnamed>");
                }
            }

            smallestMip = XRTexture.GetSmallestMipmapLevel(source2D.Width, source2D.Height, source2D.SmallestAllowedMipmapLevel);
            sampledSmallestMip = smallestMip;
            if (GetOrCreateAPIRenderObject(source2D, generateNow: true) is VkTexture2D { UsesAllocatorImage: true })
            {
                sampledSmallestMip = 0;
                useMiplessMeteringFallback = true;
                Debug.VulkanWarningEvery(
                    "Vulkan.AutoExposure.PlannerMip0Fallback2D",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Auto exposure is using filtered mipless metering for planner-backed source texture '{0}' because render-graph mip generation is not available yet.",
                    source2D.Name ?? "<unnamed>");
            }
            program = _autoExposureComputeProgram2D;
        }
        else if (sourceTex is XRTexture2DArray source2DArray)
        {
            if (generateMipmapsNow)
            {
                bool canGenerateMipmapsOutOfBand = true;
                if (GetOrCreateAPIRenderObject(source2DArray, generateNow: true) is VkTexture2DArray vkSource2DArray && vkSource2DArray.UsesAllocatorImage)
                    canGenerateMipmapsOutOfBand = false;

                if (canGenerateMipmapsOutOfBand)
                {
                    source2DArray.GenerateMipmapsGPU();
                }
                else
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.AutoExposure.SkipPlannerMipmaps2DArray",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Skipping out-of-band mipmap generation for planner-backed array source texture '{0}' to avoid layout races with render-graph barriers.",
                        source2DArray.Name ?? "<unnamed>");
                }
            }

            smallestMip = XRTexture.GetSmallestMipmapLevel(source2DArray.Width, source2DArray.Height, source2DArray.SmallestAllowedMipmapLevel);
            sampledSmallestMip = smallestMip;
            if (GetOrCreateAPIRenderObject(source2DArray, generateNow: true) is VkTexture2DArray { UsesAllocatorImage: true })
            {
                sampledSmallestMip = 0;
                useMiplessMeteringFallback = true;
                Debug.VulkanWarningEvery(
                    "Vulkan.AutoExposure.PlannerMip0Fallback2DArray",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Auto exposure is using filtered mipless metering for planner-backed array source texture '{0}' because render-graph mip generation is not available yet.",
                    source2DArray.Name ?? "<unnamed>");
            }
            layerCount = (int)Math.Max(source2DArray.Depth, 1u);
            program = _autoExposureComputeProgram2DArray;
            Debug.VulkanEvery(
                $"Vulkan.AutoExposure.HeadsetSharedArray.{source2DArray.Name ?? source2DArray.SamplerName ?? "<unnamed>"}",
                TimeSpan.FromSeconds(5),
                "[Vulkan] Auto exposure policy=HeadsetShared source='{0}' layers={1}; luminance is averaged across stereo array layers.",
                source2DArray.Name ?? source2DArray.SamplerName ?? "<unnamed>",
                layerCount);
        }
        else
        {
            return false;
        }

        if (program is null)
            return false;

        int meteringMip = sampledSmallestMip;
        if (settings.AutoExposureMetering != ColorGradingSettings.AutoExposureMeteringMode.Average)
        {
            int targetSize = Math.Clamp(settings.AutoExposureMeteringTargetSize, 1, 64);
            uint pow2 = 1u << BitOperations.Log2((uint)targetSize);
            int offset = BitOperations.Log2(pow2);
            meteringMip = Math.Clamp(sampledSmallestMip - offset, 0, sampledSmallestMip);
        }

        float alpha = 1.0f - MathF.Exp(-settings.ExposureTransitionSpeed * deltaTime);

        bool exposureLayoutManagedByRenderGraph = false;

        // Ensure standalone exposure images are in GENERAL for storage write.
        // Render-graph images already have pass-declared read/write usage and
        // must not be transitioned through an out-of-band one-shot submit.
        if (GetOrCreateAPIRenderObject(exposureTex, generateNow: true) is VkTexture2D vkExposure)
        {
            exposureLayoutManagedByRenderGraph = vkExposure.UsesAllocatorImage;
            if (exposureLayoutManagedByRenderGraph)
            {
                Debug.VulkanEvery(
                    "Vulkan.AutoExposure.PlannerExposureGraphBarriers",
                    TimeSpan.FromSeconds(5),
                    "[Vulkan] Auto exposure is relying on render-graph barriers for planner-backed exposure texture '{0}'.",
                    exposureTex.Name ?? "<unnamed>");
            }
            else
            {
                Silk.NET.Vulkan.ImageLayout oldLayout = vkExposure.CurrentImageLayout;
                if (oldLayout != Silk.NET.Vulkan.ImageLayout.General)
                    vkExposure.TransitionImageLayout(oldLayout, Silk.NET.Vulkan.ImageLayout.General);
            }
        }

        program.Uniform("SmallestMip", sampledSmallestMip);
        program.Uniform("LuminanceWeights", settings.AutoExposureLuminanceWeights);
        program.Uniform("AutoExposureBias", settings.AutoExposureBias);
        program.Uniform("AutoExposureScale", settings.AutoExposureScale);
        program.Uniform("ExposureDividend", settings.ExposureDividend);
        settings.GetResolvedExposureBounds(out float minExposure, out float maxExposure);
        program.Uniform("MinExposure", minExposure);
        program.Uniform("MaxExposure", maxExposure);
        program.Uniform("ExposureBase", settings.ExposureMode == ColorGradingSettings.ExposureControlMode.Physical
            ? settings.ComputePhysicalExposureMultiplier()
            : 1.0f);
        float fallbackExposure = settings.ExposureMode == ColorGradingSettings.ExposureControlMode.Physical
            ? settings.ComputePhysicalExposureMultiplier()
            : settings.Exposure;
        program.Uniform("FallbackExposure", Math.Clamp(fallbackExposure, minExposure, maxExposure));

        program.Uniform("MeteringMode", (int)settings.AutoExposureMetering);
        program.Uniform("MeteringMip", meteringMip);
        program.Uniform("MeteringTargetSize", settings.AutoExposureMeteringTargetSize);
        program.Uniform("UseMiplessMeteringFallback", useMiplessMeteringFallback ? 1 : 0);
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

        if (!exposureLayoutManagedByRenderGraph && GetOrCreateAPIRenderObject(exposureTex, generateNow: true) is VkTexture2D vkExposurePost)
        {
            Silk.NET.Vulkan.ImageLayout oldLayout = vkExposurePost.CurrentImageLayout;
            if (oldLayout != Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal)
                vkExposurePost.TransitionImageLayout(oldLayout, Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal);
        }

        return true;
    }

    private bool ComputeSupportsGpuAutoExposure()
    {
        // Vulkan backend requires a compute-capable queue for this path.
        return FamilyQueueIndices.ComputeFamilyIndex.HasValue;
    }

    private bool EnsureExposureStorageUsage(XRTexture2D exposureTex)
    {
        // Ensure the abstract texture carries the storage flag so the Vulkan
        // backend respects it even when a physical group overrides the usage.
        if (!exposureTex.RequiresStorageUsage)
            exposureTex.RequiresStorageUsage = true;

        if (GetOrCreateAPIRenderObject(exposureTex) is not VkTexture2D vkExposure)
            return false;

        if ((vkExposure.Usage & Silk.NET.Vulkan.ImageUsageFlags.StorageBit) != 0)
            return true;

        // The VkImage was created without STORAGE_BIT. Add it and recreate.
        vkExposure.Usage |= Silk.NET.Vulkan.ImageUsageFlags.StorageBit;

        if (vkExposure.IsGenerated)
        {
            vkExposure.Destroy();
            vkExposure.Generate();
        }

        return (vkExposure.Usage & Silk.NET.Vulkan.ImageUsageFlags.StorageBit) != 0;
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
                })
            {
                Name = "VulkanAutoExposure2D"
            };

            _autoExposureComputeProgram2DArray = new XRRenderProgram(
                linkNow: true,
                separable: false,
                new XRShader(EShaderType.Compute, AutoExposureComputeShaderSource2DArray)
                {
                    Name = "VulkanAutoExposure2DArray.comp"
                })
            {
                Name = "VulkanAutoExposure2DArray"
            };

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
    }

    private const string AutoExposureComputeShaderSource2D = @"#version 460
layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;

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
uniform float FallbackExposure;

uniform int MeteringMode;
uniform int MeteringMip;
uniform int MeteringTargetSize;
uniform int UseMiplessMeteringFallback;
uniform float IgnoreTopPercent;
uniform float CenterWeightStrength;
uniform float CenterWeightPower;

const int MAX_SAMPLES = 256;
const int SAMPLE_GRID = 16;
const int BLOCK_TAPS_PER_AXIS = 4;
const float PAD_LUM = 3.402823e38;

shared float s_lums[MAX_SAMPLES];
shared float s_weights[MAX_SAMPLES];

float SafeLum(vec3 rgb)
{
    rgb = max(rgb, vec3(0.0));
    if (any(isnan(rgb)) || any(isinf(rgb)))
        rgb = vec3(0.0);

    return max(dot(rgb, LuminanceWeights), 0.0);
}

float SampleGridLuminance(int tid, int gridX, int gridY, int w, int h, int mip)
{
    int gy = tid / gridX;
    int gx = tid - gy * gridX;

    int x0 = (gx * w) / gridX;
    int x1 = max(x0 + 1, ((gx + 1) * w) / gridX);
    int y0 = (gy * h) / gridY;
    int y1 = max(y0 + 1, ((gy + 1) * h) / gridY);
    int bw = max(x1 - x0, 1);
    int bh = max(y1 - y0, 1);

    float sum = 0.0;
    for (int ty = 0; ty < BLOCK_TAPS_PER_AXIS; ++ty)
    {
        int y = min(y0 + ((ty * 2 + 1) * bh) / (BLOCK_TAPS_PER_AXIS * 2), h - 1);
        for (int tx = 0; tx < BLOCK_TAPS_PER_AXIS; ++tx)
        {
            int x = min(x0 + ((tx * 2 + 1) * bw) / (BLOCK_TAPS_PER_AXIS * 2), w - 1);
            sum += SafeLum(texelFetch(SourceTex, ivec2(x, y), mip).rgb);
        }
    }

    return sum / float(BLOCK_TAPS_PER_AXIS * BLOCK_TAPS_PER_AXIS);
}

float SampleGridWeight(int tid, int gridX, int gridY)
{
    int gy = tid / gridX;
    int gx = tid - gy * gridX;
    vec2 uv = (vec2(gx, gy) + vec2(0.5)) / vec2(gridX, gridY);
    vec2 d = uv - vec2(0.5);
    float r = length(d) / 0.70710678;
    float center = pow(clamp(1.0 - r, 0.0, 1.0), max(CenterWeightPower, 0.1));
    return mix(1.0, center, clamp(CenterWeightStrength, 0.0, 1.0));
}

void ComputeAspectGrid(int w, int h, out int gridX, out int gridY)
{
    int target = clamp(MeteringTargetSize, 1, SAMPLE_GRID);
    if (w >= h)
    {
        gridX = min(w, target);
        gridY = max(1, min(h, (h * gridX + max(w / 2, 1)) / max(w, 1)));
    }
    else
    {
        gridY = min(h, target);
        gridX = max(1, min(w, (w * gridY + max(h / 2, 1)) / max(h, 1)));
    }
}

// Parallel tree sum over the shared array. Every invocation must call this
// so the barriers stay in uniform control flow; the total is broadcast back
// to all invocations.
float ReduceSum(int tid)
{
    for (int offset = MAX_SAMPLES >> 1; offset > 0; offset >>= 1)
    {
        if (tid < offset)
            s_lums[tid] += s_lums[tid + offset];
        barrier();
    }

    float total = s_lums[0];
    // Keep the array stable until every invocation has read the total
    // (the shared array is reused by subsequent reductions).
    barrier();
    return total;
}

float ReduceWeightedAverage(int tid)
{
    for (int offset = MAX_SAMPLES >> 1; offset > 0; offset >>= 1)
    {
        if (tid < offset)
        {
            s_lums[tid] += s_lums[tid + offset];
            s_weights[tid] += s_weights[tid + offset];
        }
        barrier();
    }

    float total = s_lums[0] / max(s_weights[0], 1e-6);
    barrier();
    return total;
}

// In-place bitonic sort (ascending) of the full shared array.
void SortShared(int tid)
{
    for (int k = 2; k <= MAX_SAMPLES; k <<= 1)
    {
        for (int j = k >> 1; j > 0; j >>= 1)
        {
            int ixj = tid ^ j;
            if (ixj > tid)
            {
                bool ascending = (tid & k) == 0;
                float a = s_lums[tid];
                float b = s_lums[ixj];
                if ((a > b) == ascending)
                {
                    s_lums[tid] = b;
                    s_lums[ixj] = a;
                }
            }
            barrier();
        }
    }
}

// Cooperative metering: each invocation fetches at most one grid sample and
// the workgroup reduces in shared memory. The previous implementation ran all
// 256 fetches plus an insertion sort on a single invocation.
float ComputeMeteredLuminance()
{
    int mip = (MeteringMode == 0)
        ? clamp(SmallestMip, 0, SmallestMip)
        : clamp(MeteringMip, 0, SmallestMip);
    ivec2 sz = textureSize(SourceTex, mip);
    int w = max(sz.x, 1);
    int h = max(sz.y, 1);
    int gridX;
    int gridY;
    ComputeAspectGrid(w, h, gridX, gridY);
    int sampleCount = clamp(gridX * gridY, 1, MAX_SAMPLES);

    int tid = int(gl_LocalInvocationIndex);
    float lum = (tid < sampleCount) ? SampleGridLuminance(tid, gridX, gridY, w, h, mip) : 0.0;

    if (UseMiplessMeteringFallback != 0 && MeteringMode == 1)
    {
        s_lums[tid] = (tid < sampleCount) ? lum : 0.0;
        barrier();
        return ReduceSum(tid) / float(sampleCount);
    }

    if (MeteringMode == 1)
    {
        s_lums[tid] = (tid < sampleCount) ? lum : 0.0;
        barrier();
        float meanLum = ReduceSum(tid) / float(sampleCount);
        float logFloor = max(meanLum * 0.25, 1e-4);
        s_lums[tid] = (tid < sampleCount) ? log2(max(lum, logFloor)) : 0.0;
        barrier();
        return exp2(ReduceSum(tid) / float(sampleCount));
    }

    if (MeteringMode == 2)
    {
        float weight = (tid < sampleCount) ? SampleGridWeight(tid, gridX, gridY) : 0.0;
        s_lums[tid] = lum * weight;
        s_weights[tid] = weight;
        barrier();
        return ReduceWeightedAverage(tid);
    }

    float drop = (MeteringMode == 0) ? 0.0 : clamp(IgnoreTopPercent, 0.0, 0.5);
    if (drop > 0.0)
    {
        // Sort so the brightest fraction can be discarded; padding sorts to the end.
        s_lums[tid] = (tid < sampleCount) ? lum : PAD_LUM;
        barrier();
        SortShared(tid);

        int keep = clamp(int(floor((1.0 - drop) * float(sampleCount))), 1, sampleCount);
        if (tid >= keep)
            s_lums[tid] = 0.0;
        barrier();

        float avg = ReduceSum(tid) / float(keep);
        return avg;
    }

    s_lums[tid] = (tid < sampleCount) ? lum : 0.0;
    barrier();

    return ReduceSum(tid) / float(sampleCount);
}

void main()
{
    float lumDot = ComputeMeteredLuminance();

    if (gl_LocalInvocationIndex != 0u)
        return;

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

    private const string AutoExposureComputeShaderSource2DArray = @"#version 460
layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;

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
uniform float FallbackExposure;

uniform int MeteringMode;
uniform int MeteringMip;
uniform int MeteringTargetSize;
uniform int UseMiplessMeteringFallback;
uniform float IgnoreTopPercent;
uniform float CenterWeightStrength;
uniform float CenterWeightPower;
uniform int LayerCount;

const int MAX_SAMPLES = 256;
const int SAMPLE_GRID = 16;
const int BLOCK_TAPS_PER_AXIS = 4;
const float PAD_LUM = 3.402823e38;

shared float s_lums[MAX_SAMPLES];
shared float s_weights[MAX_SAMPLES];

float SafeLum(vec3 rgb)
{
    rgb = max(rgb, vec3(0.0));
    if (any(isnan(rgb)) || any(isinf(rgb)))
        rgb = vec3(0.0);

    return max(dot(rgb, LuminanceWeights), 0.0);
}

float SampleGridLuminance(int tid, int gridX, int gridY, int w, int h, int mip, int layer)
{
    int gy = tid / gridX;
    int gx = tid - gy * gridX;

    int x0 = (gx * w) / gridX;
    int x1 = max(x0 + 1, ((gx + 1) * w) / gridX);
    int y0 = (gy * h) / gridY;
    int y1 = max(y0 + 1, ((gy + 1) * h) / gridY);
    int bw = max(x1 - x0, 1);
    int bh = max(y1 - y0, 1);

    float sum = 0.0;
    for (int ty = 0; ty < BLOCK_TAPS_PER_AXIS; ++ty)
    {
        int y = min(y0 + ((ty * 2 + 1) * bh) / (BLOCK_TAPS_PER_AXIS * 2), h - 1);
        for (int tx = 0; tx < BLOCK_TAPS_PER_AXIS; ++tx)
        {
            int x = min(x0 + ((tx * 2 + 1) * bw) / (BLOCK_TAPS_PER_AXIS * 2), w - 1);
            sum += SafeLum(texelFetch(SourceTex, ivec3(x, y, layer), mip).rgb);
        }
    }

    return sum / float(BLOCK_TAPS_PER_AXIS * BLOCK_TAPS_PER_AXIS);
}

float SampleGridWeight(int tid, int gridX, int gridY)
{
    int gy = tid / gridX;
    int gx = tid - gy * gridX;
    vec2 uv = (vec2(gx, gy) + vec2(0.5)) / vec2(gridX, gridY);
    vec2 d = uv - vec2(0.5);
    float r = length(d) / 0.70710678;
    float center = pow(clamp(1.0 - r, 0.0, 1.0), max(CenterWeightPower, 0.1));
    return mix(1.0, center, clamp(CenterWeightStrength, 0.0, 1.0));
}

void ComputeAspectGrid(int w, int h, out int gridX, out int gridY)
{
    int target = clamp(MeteringTargetSize, 1, SAMPLE_GRID);
    if (w >= h)
    {
        gridX = min(w, target);
        gridY = max(1, min(h, (h * gridX + max(w / 2, 1)) / max(w, 1)));
    }
    else
    {
        gridY = min(h, target);
        gridX = max(1, min(w, (w * gridY + max(h / 2, 1)) / max(h, 1)));
    }
}

// Parallel tree sum over the shared array. Every invocation must call this
// so the barriers stay in uniform control flow; the total is broadcast back
// to all invocations.
float ReduceSum(int tid)
{
    for (int offset = MAX_SAMPLES >> 1; offset > 0; offset >>= 1)
    {
        if (tid < offset)
            s_lums[tid] += s_lums[tid + offset];
        barrier();
    }

    float total = s_lums[0];
    // Keep the array stable until every invocation has read the total
    // (the shared array is reused by the next layer's reduction).
    barrier();
    return total;
}

float ReduceWeightedAverage(int tid)
{
    for (int offset = MAX_SAMPLES >> 1; offset > 0; offset >>= 1)
    {
        if (tid < offset)
        {
            s_lums[tid] += s_lums[tid + offset];
            s_weights[tid] += s_weights[tid + offset];
        }
        barrier();
    }

    float total = s_lums[0] / max(s_weights[0], 1e-6);
    barrier();
    return total;
}

// In-place bitonic sort (ascending) of the full shared array.
void SortShared(int tid)
{
    for (int k = 2; k <= MAX_SAMPLES; k <<= 1)
    {
        for (int j = k >> 1; j > 0; j >>= 1)
        {
            int ixj = tid ^ j;
            if (ixj > tid)
            {
                bool ascending = (tid & k) == 0;
                float a = s_lums[tid];
                float b = s_lums[ixj];
                if ((a > b) == ascending)
                {
                    s_lums[tid] = b;
                    s_lums[ixj] = a;
                }
            }
            barrier();
        }
    }
}

// Cooperative metering: each invocation fetches at most one grid sample per
// layer and the workgroup reduces in shared memory. The previous
// implementation ran all fetches plus an insertion sort on a single invocation.
float ComputeMeteredLuminanceForLayer(int layer)
{
    int mip = (MeteringMode == 0)
        ? clamp(SmallestMip, 0, SmallestMip)
        : clamp(MeteringMip, 0, SmallestMip);
    ivec2 sz = textureSize(SourceTex, mip).xy;
    int w = max(sz.x, 1);
    int h = max(sz.y, 1);
    int gridX;
    int gridY;
    ComputeAspectGrid(w, h, gridX, gridY);
    int sampleCount = clamp(gridX * gridY, 1, MAX_SAMPLES);

    int tid = int(gl_LocalInvocationIndex);
    float lum = (tid < sampleCount) ? SampleGridLuminance(tid, gridX, gridY, w, h, mip, layer) : 0.0;

    if (UseMiplessMeteringFallback != 0 && MeteringMode == 1)
    {
        s_lums[tid] = (tid < sampleCount) ? lum : 0.0;
        barrier();
        return ReduceSum(tid) / float(sampleCount);
    }

    if (MeteringMode == 1)
    {
        s_lums[tid] = (tid < sampleCount) ? lum : 0.0;
        barrier();
        float meanLum = ReduceSum(tid) / float(sampleCount);
        float logFloor = max(meanLum * 0.25, 1e-4);
        s_lums[tid] = (tid < sampleCount) ? log2(max(lum, logFloor)) : 0.0;
        barrier();
        return exp2(ReduceSum(tid) / float(sampleCount));
    }

    if (MeteringMode == 2)
    {
        float weight = (tid < sampleCount) ? SampleGridWeight(tid, gridX, gridY) : 0.0;
        s_lums[tid] = lum * weight;
        s_weights[tid] = weight;
        barrier();
        return ReduceWeightedAverage(tid);
    }

    float drop = (MeteringMode == 0) ? 0.0 : clamp(IgnoreTopPercent, 0.0, 0.5);
    if (drop > 0.0)
    {
        // Sort so the brightest fraction can be discarded; padding sorts to the end.
        s_lums[tid] = (tid < sampleCount) ? lum : PAD_LUM;
        barrier();
        SortShared(tid);

        int keep = clamp(int(floor((1.0 - drop) * float(sampleCount))), 1, sampleCount);
        if (tid >= keep)
            s_lums[tid] = 0.0;
        barrier();

        float avg = ReduceSum(tid) / float(keep);
        return avg;
    }

    s_lums[tid] = (tid < sampleCount) ? lum : 0.0;
    barrier();

    return ReduceSum(tid) / float(sampleCount);
}

void main()
{
    int layers = max(LayerCount, 1);
    float lumDot = 0.0;
    for (int layer = 0; layer < layers; ++layer)
        lumDot += ComputeMeteredLuminanceForLayer(layer);
    lumDot /= float(layers);

    if (gl_LocalInvocationIndex != 0u)
        return;

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
}
