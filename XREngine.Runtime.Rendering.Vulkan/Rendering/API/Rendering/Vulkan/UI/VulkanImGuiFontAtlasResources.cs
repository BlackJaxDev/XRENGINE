using ImGuiNET;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using Buffer = Silk.NET.Vulkan.Buffer;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Creates the generation-owned ImGui font atlas without using a renderer
/// facade.  It owns the one-time staging upload and publishes the resulting
/// image, sampler, and descriptor handles into the shared output state.
/// </summary>
internal unsafe sealed class VulkanImGuiFontAtlasResources(
    VulkanImGuiResources resourcesState,
    VulkanImGuiTextureRegistry textureRegistry,
    VulkanResourceRuntime resourceRuntime,
    VulkanCommandRuntime commandRuntime,
    VulkanDeviceContext deviceContext,
    VulkanTargetOutputContext target)
{
    private const uint DescriptorPoolMaxSets = 256;
    private readonly VulkanImGuiResources _resourcesState = resourcesState;
    private readonly VulkanImGuiTextureRegistry _textureRegistry = textureRegistry;
    private readonly VulkanResourceRuntime _resourceRuntime = resourceRuntime;
    private readonly VulkanCommandRuntime _commandRuntime = commandRuntime;
    private readonly VulkanDeviceContext _deviceContext = deviceContext;
    private readonly VulkanTargetOutputContext _target = target;

    internal void EnsureCreated()
    {
        VulkanImGuiResources resources = _resourcesState;
        if (resources.FontReady)
            return;

        VulkanTargetOutputContext target = _target;
        target.ThrowIfVulkanDeviceOperationNotAdmitted("ImGui.FontAtlas.Create");
        ImGui.GetIO().Fonts.GetTexDataAsRGBA32(
            out byte* pixels,
            out int width,
            out int height,
            out _);
        if (pixels == null || width <= 0 || height <= 0)
            throw new InvalidOperationException("Failed to get ImGui font atlas pixels.");

        ulong uploadSize = checked((ulong)width * (ulong)height * 4UL);
        Buffer stagingBuffer = default;
        VulkanMemoryAllocation stagingAllocation = default;
        bool stagingTracked = false;
        try
        {
            (stagingBuffer, stagingAllocation) = CreateAndUploadStagingBuffer(
                target,
                pixels,
                uploadSize);
            stagingTracked = true;
            CreateImageResources(target, resources, (uint)width, (uint)height);

            using VulkanSynchronousUploadSession upload = new(
                target.VulkanApi,
                _deviceContext,
                _commandRuntime,
                _resourceRuntime,
                "ImGui.FontAtlas.Upload");
            RecordImageUpload(upload.Encoder, upload.CommandBuffer, resources.FontImage, stagingBuffer, (uint)width, (uint)height);
            upload.CompleteAndWait(resources.FontImage, stagingBuffer, "ImGui.FontAtlas.Upload");

            _resourceRuntime.Buffers.Retire(
                stagingBuffer,
                stagingAllocation.Memory,
                "ImGui.FontAtlas.StagingBuffer");
            stagingTracked = false;
            CreateDescriptorResources(target, resources);
            resources.FontReady = true;
        }
        catch
        {
            if (stagingTracked)
                _resourceRuntime.Buffers.Retire(
                    stagingBuffer,
                    stagingAllocation.Memory,
                    "ImGui.FontAtlas.StagingBuffer.CreateFailure");
            DestroyIncompleteFontResources(target, resources);
            throw;
        }
    }

    /// <summary>
    /// Releases font-atlas state through the output/resource authorities during
    /// renderer shutdown. The image path is ticket-retired; descriptor objects
    /// are only destroyed after command recording has stopped.
    /// </summary>
    internal void RetireAll()
    {
        VulkanImGuiResources resources = _resourcesState;
        if (resources.FontImage.Handle != 0 || resources.FontImageView.Handle != 0 || resources.FontSampler.Handle != 0)
        {
            _resourceRuntime.Images.RetireOwnedResources(new RetiredImageResources(
                resources.FontImage,
                resources.FontImageMemory,
                resources.FontImageView,
                [],
                resources.FontSampler,
                0));
        }

        VulkanTargetOutputContext target = _target;
        if (resources.DescriptorPool.Handle != 0)
            _resourceRuntime.DescriptorLifetime.RetireDescriptorPool(resources.DescriptorPool);
        if (resources.DescriptorSetLayout.Handle != 0)
        {
            _resourceRuntime.DestroyDescriptorSetLayout(
                target.VulkanApi,
                target.Device,
                _resourceRuntime.FramebufferRetirementFrameSlot,
                resources.DescriptorSetLayout,
                "ImGui.FontAtlas.DescriptorSetLayout");
        }

        resources.FontImage = default;
        resources.FontImageMemory = default;
        resources.FontImageView = default;
        resources.FontSampler = default;
        resources.DescriptorPool = default;
        resources.DescriptorSetLayout = default;
        resources.FontDescriptorSet = default;
        resources.FontReady = false;
        _textureRegistry.Clear();
    }

    private (Buffer Buffer, VulkanMemoryAllocation Allocation) CreateAndUploadStagingBuffer(
        VulkanTargetOutputContext target,
        byte* source,
        ulong size)
    {
        BufferCreateInfo createInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };
        Result result = target.VulkanApi.CreateBuffer(target.Device, ref createInfo, null, out Buffer buffer);
        target.ObserveNativeResult("vkCreateBuffer.ImGuiFontAtlas", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create ImGui font staging buffer ({result}).");

        target.TrackLiveBuffer(buffer, "ImGui.FontAtlas.StagingBuffer");
        VulkanMemoryAllocation allocation = default;
        try
        {
            allocation = target.AllocateBufferMemoryWithFallback(
                buffer,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            target.TrackExternalBufferAllocation(buffer, in allocation);
            result = target.VulkanApi.BindBufferMemory(target.Device, buffer, allocation.Memory, allocation.Offset);
            target.ObserveNativeResult("vkBindBufferMemory.ImGuiFontAtlas", result);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to bind ImGui font staging memory ({result}).");

            if (!target.TryWriteMappedMemory(allocation, 0, size, (nint)source, static (destination, state) =>
                    new ReadOnlySpan<byte>((void*)state, destination.Length).CopyTo(destination)))
                throw new InvalidOperationException("Failed to map ImGui font staging memory.");

            return (buffer, allocation);
        }
        catch
        {
            _resourceRuntime.Buffers.Retire(
                buffer,
                allocation.Memory,
                "ImGui.FontAtlas.StagingBuffer.CreateFailure");
            throw;
        }
    }

    private void CreateImageResources(
        VulkanTargetOutputContext target,
        VulkanImGuiResources resources,
        uint width,
        uint height)
    {
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = Format.R8G8B8A8Unorm,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };
        Result result = target.CreateVulkanImageTracked(
            ref imageInfo,
            out resources.FontImage,
            "ImGui.FontAtlas.Image");
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create ImGui font image ({result}).");

        VulkanMemoryAllocation allocation = target.AllocateImageMemoryWithFallback(
            resources.FontImage,
            MemoryPropertyFlags.DeviceLocalBit);
        _resourceRuntime.Allocations.Images.Allocations[resources.FontImage.Handle] = allocation;
        resources.FontImageMemory = allocation.Memory;
        result = target.VulkanApi.BindImageMemory(
            target.Device,
            resources.FontImage,
            allocation.Memory,
            allocation.Offset);
        target.ObserveNativeResult("vkBindImageMemory.ImGuiFontAtlas", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to bind ImGui font image memory ({result}).");

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = resources.FontImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        result = target.VulkanApi.CreateImageView(target.Device, ref viewInfo, null, out resources.FontImageView);
        target.ObserveNativeResult("vkCreateImageView.ImGuiFontAtlas", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create ImGui font image view ({result}).");
        target.TrackLiveImageView(resources.FontImageView, in viewInfo, "ImGui.FontAtlas.ImageView");

        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MaxAnisotropy = 1f,
            CompareOp = CompareOp.Always,
            MaxLod = 0f,
            BorderColor = BorderColor.FloatOpaqueWhite,
        };
        result = target.VulkanApi.CreateSampler(target.Device, ref samplerInfo, null, out resources.FontSampler);
        target.ObserveNativeResult("vkCreateSampler.ImGuiFontAtlas", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create ImGui font sampler ({result}).");
        _resourceRuntime.Samplers.Register(resources.FontSampler, in samplerInfo, "ImGui.FontAtlas.Sampler");
    }

    private static void RecordImageUpload(
        VulkanTrackedCommandEncoder encoder,
        CommandBuffer commandBuffer,
        Image image,
        Buffer stagingBuffer,
        uint width,
        uint height)
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            LevelCount = 1,
            LayerCount = 1,
        };
        ImageMemoryBarrier toTransfer = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            DstAccessMask = AccessFlags.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
        encoder.PipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toTransfer);

        BufferImageCopy copy = new()
        {
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LayerCount = 1,
            },
            ImageExtent = new Extent3D(width, height, 1),
        };
        encoder.CopyBufferToImage(
            commandBuffer,
            stagingBuffer,
            image,
            ImageLayout.TransferDstOptimal,
            1,
            &copy);

        ImageMemoryBarrier toSampled = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
        encoder.PipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toSampled);
    }

    private void CreateDescriptorResources(VulkanTargetOutputContext target, VulkanImGuiResources resources)
    {
        DescriptorSetLayoutBinding binding = new()
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        Result result = target.VulkanApi.CreateDescriptorSetLayout(
            target.Device,
            ref layoutInfo,
            null,
            out resources.DescriptorSetLayout);
        target.ObserveNativeResult("vkCreateDescriptorSetLayout.ImGuiFontAtlas", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create ImGui descriptor set layout ({result}).");
        _resourceRuntime.Descriptors.LiveDescriptorSetLayoutHandles[resources.DescriptorSetLayout.Handle] =
            "ImGui.FontAtlas.DescriptorSetLayout";
        _resourceRuntime.Lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.DescriptorSetLayout, resources.DescriptorSetLayout.Handle),
            "ImGui.FontAtlas.DescriptorSetLayout",
            externallyOwned: false);

        DescriptorPoolSize poolSize = new()
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = DescriptorPoolMaxSets,
        };
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = DescriptorPoolMaxSets,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
        };
        result = target.VulkanApi.CreateDescriptorPool(target.Device, ref poolInfo, null, out resources.DescriptorPool);
        target.ObserveNativeResult("vkCreateDescriptorPool.ImGuiFontAtlas", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create ImGui descriptor pool ({result}).");
        _resourceRuntime.Lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.DescriptorPool, resources.DescriptorPool.Handle),
            "ImGui.FontAtlas.DescriptorPool",
            externallyOwned: false);

        DescriptorSetLayout layout = resources.DescriptorSetLayout;
        DescriptorSetAllocateInfo allocation = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = resources.DescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        result = target.VulkanApi.AllocateDescriptorSets(target.Device, ref allocation, out resources.FontDescriptorSet);
        target.ObserveNativeResult("vkAllocateDescriptorSets.ImGuiFontAtlas", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to allocate ImGui font descriptor set ({result}).");
        _resourceRuntime.Lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.DescriptorSet, resources.FontDescriptorSet.Handle),
            "ImGui.FontAtlas.DescriptorSet",
            externallyOwned: false);

        DescriptorImageInfo imageInfo = new()
        {
            Sampler = resources.FontSampler,
            ImageView = resources.FontImageView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = resources.FontDescriptorSet,
            DstBinding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfo,
        };
        target.VulkanApi.UpdateDescriptorSets(target.Device, 1, &write, 0, null);
        _textureRegistry.DescriptorSets[(nint)1] = resources.FontDescriptorSet;
    }

    private void DestroyIncompleteFontResources(VulkanTargetOutputContext target, VulkanImGuiResources resources)
    {
        Vk api = target.VulkanApi;
        if (resources.DescriptorPool.Handle != 0)
            _resourceRuntime.DescriptorLifetime.RetireDescriptorPool(resources.DescriptorPool);
        if (resources.DescriptorSetLayout.Handle != 0)
        {
            _resourceRuntime.DestroyDescriptorSetLayout(
                api,
                target.Device,
                _resourceRuntime.FramebufferRetirementFrameSlot,
                resources.DescriptorSetLayout,
                "ImGui.FontAtlas.CreateFailure.DescriptorSetLayout");
        }
        if (resources.FontSampler.Handle != 0)
            api.DestroySampler(target.Device, resources.FontSampler, null);
        if (resources.FontImageView.Handle != 0 && target.TryBeginDestroyImageView(resources.FontImageView, "ImGui.FontAtlas.CreateFailure"))
            api.DestroyImageView(target.Device, resources.FontImageView, null);
        if (resources.FontImage.Handle != 0)
        {
            _resourceRuntime.Allocations.Images.Allocations.TryRemove(
                resources.FontImage.Handle,
                out VulkanMemoryAllocation allocation);
            target.DestroyVulkanImageImmediateTracked(resources.FontImage, "ImGui.FontAtlas.CreateFailure");
            if (allocation.Memory.Handle != 0)
                target.FreeMemoryAllocation(allocation);
        }

        resources.FontDescriptorSet = default;
        resources.DescriptorPool = default;
        resources.DescriptorSetLayout = default;
        resources.FontSampler = default;
        resources.FontImageView = default;
        resources.FontImage = default;
        resources.FontImageMemory = default;
        resources.FontReady = false;
    }
}
