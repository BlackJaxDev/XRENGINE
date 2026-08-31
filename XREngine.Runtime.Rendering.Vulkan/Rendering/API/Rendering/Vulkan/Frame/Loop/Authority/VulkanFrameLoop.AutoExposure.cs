using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>Coordinates GPU auto-exposure work with the current frame-operation stream.</summary>
internal sealed partial class VulkanFrameLoop
{
    internal bool SupportsGpuAutoExposure
    {
        get
        {
            if (_resourceRuntime.SupportsGpuAutoExposure == false)
                return false;

            _resourceRuntime.SupportsGpuAutoExposure ??= ComputeSupportsGpuAutoExposure();
            if (_resourceRuntime.SupportsGpuAutoExposure != true)
                return false;

            if (!_resourceRuntime.EnsureAutoExposureComputeResources())
                _resourceRuntime.SupportsGpuAutoExposure = false;

            return _resourceRuntime.AutoExposureComputeInitialized;
        }
    }

    internal bool UpdateAutoExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex, ColorGradingSettings settings, float deltaTime, bool generateMipmapsNow)
    {
        if (!_resourceRuntime.EnsureAutoExposureComputeResources())
        {
            _resourceRuntime.SupportsGpuAutoExposure = false;
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
                        TimeSpan.FromSeconds(30),
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
                    TimeSpan.FromSeconds(30),
                    "[Vulkan] Auto exposure is using filtered mipless metering for planner-backed source texture '{0}' because render-graph mip generation is not available yet.",
                    source2D.Name ?? "<unnamed>");
            }
            program = _resourceRuntime.AutoExposureComputeProgram2D;
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
                        TimeSpan.FromSeconds(30),
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
                    TimeSpan.FromSeconds(30),
                    "[Vulkan] Auto exposure is using filtered mipless metering for planner-backed array source texture '{0}' because render-graph mip generation is not available yet.",
                    source2DArray.Name ?? "<unnamed>");
            }
            layerCount = (int)Math.Max(source2DArray.Depth, 1u);
            program = _resourceRuntime.AutoExposureComputeProgram2DArray;
            Debug.VulkanEvery(
                $"Vulkan.AutoExposure.HeadsetSharedArray.{source2DArray.Name ?? source2DArray.SamplerName ?? "<unnamed>"}",
                TimeSpan.FromSeconds(30),
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
                VulkanPhysicalImageGroup? activeExposureGroup =
                    vkExposure.TryResolvePhysicalGroup(ensureAllocated: false);
                if (VulkanFramePlanner.IsUsableAutoExposureHistoryGroup(activeExposureGroup))
                    _framePlanner.TrackAutoExposureHistory(activeExposureGroup!);

                Debug.VulkanEvery(
                    "Vulkan.AutoExposure.PlannerExposureGraphBarriers",
                    TimeSpan.FromSeconds(30),
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

        if (TryDispatchCompute(program, 1, 1, 1) != ERendererComputeEnqueueStatus.Enqueued)
            return false;
        EnqueueMemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.TextureFetch);

        if (!exposureLayoutManagedByRenderGraph && GetOrCreateAPIRenderObject(exposureTex, generateNow: true) is VkTexture2D vkExposurePost)
        {
            Silk.NET.Vulkan.ImageLayout oldLayout = vkExposurePost.CurrentImageLayout;
            if (oldLayout != Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal)
                vkExposurePost.TransitionImageLayout(oldLayout, Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal);
        }

        return true;
    }


    internal ERendererComputeEnqueueStatus TryDispatchCompute(
        XRRenderProgram program,
        uint groupsX,
        uint groupsY,
        uint groupsZ)
        {
        if (!_deviceContext.IsOperational)
            return ERendererComputeEnqueueStatus.DeviceLost;
        if (program is null)
            return ERendererComputeEnqueueStatus.InvalidResource;

        uint x = Math.Max(groupsX, 1u);
        uint y = Math.Max(groupsY, 1u);
        uint z = Math.Max(groupsZ, 1u);

        if (_resourceRuntime.CreateAPIRenderObject(program) is not VkRenderProgram vkProgram)
        {
            Debug.VulkanWarning("DispatchCompute skipped: program could not be resolved to VkRenderProgram.");
            return ERendererComputeEnqueueStatus.InvalidResource;
        }

        vkProgram.Generate();
        if (!vkProgram.Link(program.AllowAsyncBackendCompile))
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.DispatchCompute.ProgramPending.{RuntimeHelpers.GetHashCode(program)}",
                TimeSpan.FromSeconds(1),
                "DispatchCompute deferred: program '{0}' is not ready.",
                program.Name ?? "UnnamedProgram");
            return ERendererComputeEnqueueStatus.ProgramPending;
        }

        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        string programName = string.IsNullOrWhiteSpace(program.Name) ? "UnnamedProgram" : program.Name;
        string opName = _frameTelemetry.ComputeDispatchOperationNames.GetOrAdd(
            programName,
            static name => string.Concat("DispatchCompute:", name));
        int passIndex = VulkanCommandRuntime.ResolveOrderedPrimaryWorkPassIndex(
            opName,
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
            context.PassMetadata);
        if (passIndex == int.MinValue)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.DispatchCompute.NoPass.{programName}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] DispatchCompute skipped for '{0}' because no active render-graph pass could be resolved.",
                programName);
            return ERendererComputeEnqueueStatus.NoPassContext;
        }

        ComputeDispatchSnapshot snapshot = vkProgram.CaptureComputeSnapshot();
        if (!vkProgram.ValidateComputeSnapshot(snapshot, out string? descriptorFailure))
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.DispatchCompute.DescriptorInvalid.{RuntimeHelpers.GetHashCode(program)}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] DispatchCompute skipped for '{0}' because its descriptor snapshot is invalid: {1}",
                programName,
                descriptorFailure ?? "unknown descriptor failure");
            return ERendererComputeEnqueueStatus.DescriptorInvalid;
        }

        // The sealed frame-plan preparation owns native compute-pipeline
        // readiness. Admission must preserve this dispatch while that request
        // is Pending; dropping it here would make an async compile invisible.
        EnqueueFrameOp(ComputeDispatchOp.Rent(
            passIndex,
            vkProgram,
            x,
            y,
            z,
            snapshot,
            context));
        return ERendererComputeEnqueueStatus.Enqueued;
        }

    private bool ComputeSupportsGpuAutoExposure()
        => _deviceContext.QueueFamilies.ComputeFamilyIndex.HasValue;

    private bool EnsureExposureStorageUsage(XRTexture2D exposureTex)
    {
        if (!exposureTex.RequiresStorageUsage)
            exposureTex.RequiresStorageUsage = true;

        if (GetOrCreateAPIRenderObject(exposureTex) is not VkTexture2D vkExposure)
            return false;

        if ((vkExposure.Usage & Silk.NET.Vulkan.ImageUsageFlags.StorageBit) != 0)
            return true;

        vkExposure.Usage |= Silk.NET.Vulkan.ImageUsageFlags.StorageBit;
        if (vkExposure.IsGenerated)
        {
            vkExposure.Destroy();
            vkExposure.Generate();
        }

        return (vkExposure.Usage & Silk.NET.Vulkan.ImageUsageFlags.StorageBit) != 0;
    }

    internal void EnqueueMemoryBarrier(EMemoryBarrierMask mask)
    {
        if (mask == EMemoryBarrierMask.None)
            return;

        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        int passIndex = VulkanCommandRuntime.EnsureValidPassIndex(
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
            "MemoryBarrier",
            context.PassMetadata);
        if (passIndex == int.MinValue)
        {
            _commandRuntime.ActiveState.RegisterMemoryBarrier(mask);
            _commandRuntime.MarkCommandBuffersDirty();
            return;
        }

        EnqueueFrameOp(VulkanCommandRuntime.CreateMemoryBarrierOperation(passIndex, mask, context));
    }
}
