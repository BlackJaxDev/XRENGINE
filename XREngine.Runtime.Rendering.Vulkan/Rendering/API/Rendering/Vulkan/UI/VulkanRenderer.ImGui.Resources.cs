using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private void DestroyImGuiDrawBuffers()
    {
        for (int i = 0; i < _imguiResources.DrawBuffers.Length; i++)
        {
            ref VulkanImGuiDrawBufferSet buffers = ref _imguiResources.DrawBuffers[i];

            if (buffers.VertexBuffer.Handle != 0)
            {
                DestroyBuffer(buffers.VertexBuffer, buffers.VertexBufferMemory);
                buffers.VertexBuffer = default;
                buffers.VertexBufferMemory = default;
                buffers.VertexBufferSize = 0;
            }

            if (buffers.IndexBuffer.Handle != 0)
            {
                DestroyBuffer(buffers.IndexBuffer, buffers.IndexBufferMemory);
                buffers.IndexBuffer = default;
                buffers.IndexBufferMemory = default;
                buffers.IndexBufferSize = 0;
            }
        }

        _imguiResources.DrawBuffers = [];
    }

    private void DestroyImGuiPipelineResources()
    {
        if (Api is null)
            return;

        if (_imguiResources.Pipeline.Handle != 0)
            RetirePipeline(_imguiResources.Pipeline);
        _imguiResources.Pipeline = default;

        if (_imguiResources.PipelineLayout.Handle != 0)
        {
            PipelineLayout pipelineLayout = _imguiResources.PipelineLayout;
            _imguiResources.PipelineLayout = default;
            if (TryBeginDestroyPipelineLayout(pipelineLayout, "ImGui.DestroyPipelineResources"))
            {
                Api.DestroyPipelineLayout(device, pipelineLayout, null);
            }
        }

        if (_imguiResources.VertShader.Handle != 0)
            Api.DestroyShaderModule(device, _imguiResources.VertShader, null);
        _imguiResources.VertShader = default;

        if (_imguiResources.FragShader.Handle != 0)
            Api.DestroyShaderModule(device, _imguiResources.FragShader, null);
        _imguiResources.FragShader = default;

        _imguiResources.PipelineSignature = 0;
    }

    private void DestroyImGuiFontResources()
    {
        if (Api is null)
            return;

        RetireImageResources(new RetiredImageResources(
            _imguiResources.FontImage,
            _imguiResources.FontImageMemory,
            _imguiResources.FontImageView,
            [],
            _imguiResources.FontSampler,
            0));
        _imguiResources.FontSampler = default;
        _imguiResources.FontImageView = default;
        _imguiResources.FontImage = default;
        _imguiResources.FontImageMemory = default;

        if (_imguiResources.DescriptorPool.Handle != 0)
            RetireDescriptorPool(_imguiResources.DescriptorPool);
        _imguiResources.DescriptorPool = default;

        if (_imguiResources.DescriptorSetLayout.Handle != 0 &&
            TryBeginDestroyDescriptorSetLayout(_imguiResources.DescriptorSetLayout, "ImGui.DescriptorSetLayout"))
        {
            Api.DestroyDescriptorSetLayout(device, _imguiResources.DescriptorSetLayout, null);
        }
        _imguiResources.DescriptorSetLayout = default;

        _imguiResources.FontDescriptorSet = default;
        _imguiTextureRegistry.DescriptorSets.Clear();
        _imguiTextureRegistry.DescriptorHeapPushData.Clear();
        _imguiTextureRegistry.Registrations.Clear();
        _imguiTextureRegistry.TexturesById.Clear();
        _imguiTextureRegistry.NextTextureId = 2;
        _imguiResources.FontReady = false;
    }

    private void EnsureImGuiFontResources()
    {
        if (_imguiResources.FontReady)
            return;

        ImGuiIOPtr io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out _);
        if (pixels == null || width <= 0 || height <= 0)
            throw new InvalidOperationException("Failed to get ImGui font atlas pixels.");

        ulong uploadSize = (ulong)(width * height * 4);

        (Buffer stagingBuffer, DeviceMemory stagingMemory) = CreateBufferRaw(
            uploadSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        try
        {
            UploadBufferMemory(stagingBuffer, stagingMemory, uploadSize, pixels);
            CreateImGuiFontImage((uint)width, (uint)height);

            using (var scope = NewCommandScope())
            {
                TransitionImGuiFontImage(scope.CommandBuffer, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);

                BufferImageCopy copyRegion = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    ImageOffset = new Offset3D(0, 0, 0),
                    ImageExtent = new Extent3D((uint)width, (uint)height, 1)
                };

                CmdCopyBufferToImageTracked(scope.CommandBuffer, stagingBuffer, _imguiResources.FontImage, ImageLayout.TransferDstOptimal, 1, &copyRegion);
                TransitionImGuiFontImage(scope.CommandBuffer, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            }

            CreateImGuiFontDescriptorResources();
            io.Fonts.SetTexID((IntPtr)1);
            _imguiResources.FontReady = true;
        }
        finally
        {
            DestroyBuffer(stagingBuffer, stagingMemory);
        }
    }

    private void CreateImGuiFontImage(uint width, uint height)
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
            SharingMode = SharingMode.Exclusive
        };

        if (CreateVulkanImageTracked(ref imageInfo, out _imguiResources.FontImage, "ImGui.FontAtlas") != Result.Success)
            throw new InvalidOperationException("Failed to create ImGui font image.");

        ClearTrackedImageLayouts(_imguiResources.FontImage);
        VulkanMemoryAllocation allocation = AllocateImageMemoryWithFallback(_imguiResources.FontImage, MemoryPropertyFlags.DeviceLocalBit);
        _imageAllocationTracker.Allocations[_imguiResources.FontImage.Handle] = allocation;
        _imguiResources.FontImageMemory = allocation.Memory;

        if (Api.BindImageMemory(device, _imguiResources.FontImage, _imguiResources.FontImageMemory, allocation.Offset) != Result.Success)
        {
            _imageAllocationTracker.Allocations.TryRemove(_imguiResources.FontImage.Handle, out _);
            DestroyVulkanImageImmediateTracked(_imguiResources.FontImage, "ImGui.FontAtlas.BindFailure");
            FreeMemoryAllocation(allocation);
            _imguiResources.FontImage = default;
            _imguiResources.FontImageMemory = default;
            throw new InvalidOperationException("Failed to bind ImGui font image memory.");
        }

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _imguiResources.FontImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        if (Api.CreateImageView(device, ref viewInfo, null, out _imguiResources.FontImageView) != Result.Success)
        {
            _imageAllocationTracker.Allocations.TryRemove(_imguiResources.FontImage.Handle, out _);
            DestroyVulkanImageImmediateTracked(_imguiResources.FontImage, "ImGui.FontAtlas.ViewFailure");
            FreeMemoryAllocation(allocation);
            _imguiResources.FontImage = default;
            _imguiResources.FontImageMemory = default;
            throw new InvalidOperationException("Failed to create ImGui font image view.");
        }
        TrackLiveImageView(_imguiResources.FontImageView, in viewInfo, "ImGui.FontAtlas");

        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MipLodBias = 0f,
            AnisotropyEnable = Vk.False,
            MaxAnisotropy = 1f,
            CompareEnable = Vk.False,
            CompareOp = CompareOp.Always,
            MinLod = 0f,
            MaxLod = 0f,
            BorderColor = BorderColor.FloatOpaqueWhite,
            UnnormalizedCoordinates = Vk.False
        };

        if (Api.CreateSampler(device, ref samplerInfo, null, out _imguiResources.FontSampler) != Result.Success)
            throw new InvalidOperationException("Failed to create ImGui font sampler.");

        RegisterLiveSampler(_imguiResources.FontSampler, in samplerInfo);
    }

    private void CreateImGuiFontDescriptorResources()
    {
        DescriptorSetLayoutBinding samplerBinding = new()
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = null
        };

        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &samplerBinding
        };

        if (Api!.CreateDescriptorSetLayout(device, ref layoutInfo, null, out _imguiResources.DescriptorSetLayout) != Result.Success)
            throw new InvalidOperationException("Failed to create ImGui descriptor set layout.");
        TrackLiveDescriptorSetLayout(_imguiResources.DescriptorSetLayout, "ImGui.DescriptorSetLayout");

        DescriptorPoolSize poolSize = new()
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = ImGuiDescriptorPoolMaxSets
        };

        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = ImGuiDescriptorPoolMaxSets,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit
        };

        if (Api.CreateDescriptorPool(device, ref poolInfo, null, out _imguiResources.DescriptorPool) != Result.Success)
            throw new InvalidOperationException("Failed to create ImGui descriptor pool.");

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolCreate();

        DescriptorSetLayout descriptorLayout = _imguiResources.DescriptorSetLayout;
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _imguiResources.DescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &descriptorLayout
        };

        if (Api.AllocateDescriptorSets(device, ref allocInfo, out _imguiResources.FontDescriptorSet) != Result.Success)
            throw new InvalidOperationException("Failed to allocate ImGui descriptor set.");

        RegisterVulkanDescriptorSet(
            _imguiResources.DescriptorPool,
            _imguiResources.FontDescriptorSet,
            usesUpdateAfterBind: false,
            "ImGui.Font.DescriptorSet");
        SetDebugDescriptorSetName(_imguiResources.FontDescriptorSet, "ImGui.Font.DescriptorSet");
        RecordVulkanDescriptorTableGeneration("ImGui.FontDescriptorSet.Allocated");
        _imguiTextureRegistry.DescriptorSets[(nint)1] = _imguiResources.FontDescriptorSet;

        DescriptorImageInfo imageInfo = new()
        {
            Sampler = _imguiResources.FontSampler,
            ImageView = _imguiResources.FontImageView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _imguiResources.FontDescriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfo
        };

        UpdateDescriptorSetsTracked(1, &write);
        RecordVulkanDescriptorTableGeneration("ImGui.FontDescriptorSet.Update");
        UpdateImGuiDescriptorHeapPayload((nint)1, imageInfo);
    }

    private void TransitionImGuiFontImage(CommandBuffer commandBuffer, ImageLayout oldLayout, ImageLayout newLayout)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _imguiResources.FontImage,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        PipelineStageFlags srcStage;
        PipelineStageFlags dstStage;

        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TransferBit;
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }
        else
        {
            throw new InvalidOperationException($"Unsupported ImGui image layout transition {oldLayout} -> {newLayout}.");
        }

        CmdPipelineBarrierTracked(commandBuffer, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private void EnsureImGuiPipeline()
    {
        HashCode pipelineKeyHash = new();
        pipelineKeyHash.Add(UseDynamicRenderingRenderTargets);
        pipelineKeyHash.Add(_renderPass.Handle);
        pipelineKeyHash.Add((int)swapChainImageFormat);
        pipelineKeyHash.Add((int)swapChainImageColorSpace);
        pipelineKeyHash.Add((int)_swapchainDepthFormat);
        ulong currentPipelineSignature = unchecked((ulong)pipelineKeyHash.ToHashCode());

        if (_imguiResources.Pipeline.Handle != 0 && _imguiResources.PipelineSignature == currentPipelineSignature)
            return;

        DestroyImGuiPipelineResources();
        _imguiResources.PipelineSignature = currentPipelineSignature;

        const string vertSource = "#version 450\n"
            + "layout(push_constant) uniform PushConstants { vec2 scale; vec2 translate; } pc;\n"
            + "layout(location = 0) in vec2 inPos;\n"
            + "layout(location = 1) in vec2 inUv;\n"
            + "layout(location = 2) in vec4 inColor;\n"
            + "layout(location = 0) out vec2 outUv;\n"
            + "layout(location = 1) out vec4 outColor;\n"
            + "void main()\n"
            + "{\n"
            + "    outUv = inUv;\n"
            + "    outColor = inColor;\n"
            + "    gl_Position = vec4(inPos * pc.scale + pc.translate, 0.0, 1.0);\n"
            + "}\n";

        bool emulateOpenGlSrgbPassthrough = ShouldEmulateOpenGlImGuiSrgbPassthrough();
        string fragSource = "#version 450\n"
            + "layout(set = 0, binding = 0) uniform sampler2D sTexture;\n"
            + "layout(location = 0) in vec2 inUv;\n"
            + "layout(location = 1) in vec4 inColor;\n"
            + "layout(location = 0) out vec4 outColor;\n"
            + "vec3 SrgbToLinear(vec3 c)\n"
            + "{\n"
            + "    bvec3 cutoff = lessThanEqual(c, vec3(0.04045));\n"
            + "    vec3 low = c / 12.92;\n"
            + "    vec3 high = pow((c + vec3(0.055)) / 1.055, vec3(2.4));\n"
            + "    return mix(high, low, cutoff);\n"
            + "}\n"
            + "void main()\n"
            + "{\n"
            + "    vec4 color = inColor * texture(sTexture, inUv);\n"
            + (emulateOpenGlSrgbPassthrough
                ? "    color.rgb = SrgbToLinear(color.rgb * color.a);\n"
                : string.Empty)
            + "    outColor = color;\n"
            + "}\n";

        XRShader vs = new(EShaderType.Vertex, vertSource) { Name = "VkImGui.vs" };
        XRShader fs = new(EShaderType.Fragment, fragSource) { Name = "VkImGui.fs" };

        byte[] vsSpv = VulkanShaderCompiler.Compile(vs, out _, out _, out _);
        byte[] fsSpv = VulkanShaderCompiler.Compile(fs, out _, out _, out _);

        fixed (byte* vsPtr = vsSpv)
        fixed (byte* fsPtr = fsSpv)
        {
            ShaderModuleCreateInfo vsInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)vsSpv.Length,
                PCode = (uint*)vsPtr
            };

            ShaderModuleCreateInfo fsInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)fsSpv.Length,
                PCode = (uint*)fsPtr
            };

            if (Api!.CreateShaderModule(device, ref vsInfo, null, out _imguiResources.VertShader) != Result.Success)
                throw new InvalidOperationException("Failed to create ImGui vertex shader module.");
            if (Api.CreateShaderModule(device, ref fsInfo, null, out _imguiResources.FragShader) != Result.Success)
                throw new InvalidOperationException("Failed to create ImGui fragment shader module.");
        }

        PushConstantRange pushRange = new()
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<VulkanImGuiPushConstants>()
        };

        DescriptorSetLayout descriptorLayoutForPipeline = _imguiResources.DescriptorSetLayout;
        PipelineLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorLayoutForPipeline,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange
        };

        if (Api.CreatePipelineLayout(device, ref layoutInfo, null, out _imguiResources.PipelineLayout) != Result.Success)
            throw new InvalidOperationException("Failed to create ImGui pipeline layout.");
        TrackLivePipelineLayout(_imguiResources.PipelineLayout, "ImGui.PipelineLayout");

        PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _imguiResources.VertShader,
            PName = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main"),
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _imguiResources.FragShader,
            PName = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main"),
        };

        DescriptorSetAndBindingMappingEXTNative imguiHeapMapping = default;
        ShaderDescriptorSetAndBindingMappingInfoEXTNative imguiHeapMappingInfo = default;
        if (IsDescriptorHeapDrawBindingActive)
        {
            imguiHeapMapping = new DescriptorSetAndBindingMappingEXTNative
            {
                SType = VulkanDescriptorHeapExt.DescriptorSetAndBindingMappingSType,
                PNext = null,
                DescriptorSet = 0,
                FirstBinding = 0,
                BindingCount = 1,
                ResourceMask = VulkanSpirvResourceTypeFlagsEXT.All,
                Source = VulkanDescriptorMappingSourceEXT.HeapWithPushIndex,
                SourceData = new DescriptorMappingSourceDataEXTNative
                {
                    PushIndex = new DescriptorMappingSourcePushIndexEXTNative
                    {
                        HeapOffset = 0,
                        PushOffset = 0,
                        HeapIndexStride = DescriptorHeapSampledImageStride,
                        HeapArrayStride = DescriptorHeapSampledImageStride,
                        EmbeddedSampler = null,
                        UseCombinedImageSamplerIndex = Vk.False,
                        SamplerHeapOffset = 0,
                        SamplerPushOffset = sizeof(uint),
                        SamplerHeapIndexStride = DescriptorHeapSamplerStride,
                        SamplerHeapArrayStride = DescriptorHeapSamplerStride,
                    },
                },
            };
            imguiHeapMappingInfo = new ShaderDescriptorSetAndBindingMappingInfoEXTNative
            {
                SType = VulkanDescriptorHeapExt.ShaderDescriptorSetAndBindingMappingInfoSType,
                PNext = stages[1].PNext,
                MappingCount = 1,
                Mappings = &imguiHeapMapping,
            };
            stages[1].PNext = &imguiHeapMappingInfo;
        }

        try
        {
            VertexInputBindingDescription binding = new()
            {
                Binding = 0,
                Stride = (uint)sizeof(ImDrawVert),
                InputRate = VertexInputRate.Vertex
            };

            VertexInputAttributeDescription* attributes = stackalloc VertexInputAttributeDescription[3];
            attributes[0] = new VertexInputAttributeDescription
            {
                Location = 0,
                Binding = 0,
                Format = Format.R32G32Sfloat,
                Offset = (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.pos))
            };
            attributes[1] = new VertexInputAttributeDescription
            {
                Location = 1,
                Binding = 0,
                Format = Format.R32G32Sfloat,
                Offset = (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.uv))
            };
            attributes[2] = new VertexInputAttributeDescription
            {
                Location = 2,
                Binding = 0,
                Format = Format.R8G8B8A8Unorm,
                Offset = (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.col))
            };

            PipelineVertexInputStateCreateInfo vertexInput = new()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 3,
                PVertexAttributeDescriptions = attributes
            };

            PipelineInputAssemblyStateCreateInfo inputAssembly = new()
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = Vk.False,
            };

            PipelineViewportStateCreateInfo viewportState = new()
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };

            PipelineRasterizationStateCreateInfo rasterizer = new()
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = Vk.False,
                RasterizerDiscardEnable = Vk.False,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                DepthBiasEnable = Vk.False,
                LineWidth = 1.0f
            };

            PipelineMultisampleStateCreateInfo multisampling = new()
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
                SampleShadingEnable = Vk.False
            };

            PipelineDepthStencilStateCreateInfo depthStencil = new()
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = Vk.False,
                DepthWriteEnable = Vk.False,
                DepthCompareOp = CompareOp.Always,
                DepthBoundsTestEnable = Vk.False,
                StencilTestEnable = Vk.False
            };

            PipelineColorBlendAttachmentState colorAttachment = new()
            {
                BlendEnable = Vk.True,
                SrcColorBlendFactor = emulateOpenGlSrgbPassthrough ? BlendFactor.One : BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };

            const uint kDynRenderColorSlots = 1;
            PipelineColorBlendAttachmentState* blendSlots = stackalloc PipelineColorBlendAttachmentState[(int)kDynRenderColorSlots];
            blendSlots[0] = colorAttachment;

            uint imguiBlendCount = UseDynamicRenderingRenderTargets ? kDynRenderColorSlots : 1;

            PipelineColorBlendStateCreateInfo colorBlendState = new()
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = Vk.False,
                AttachmentCount = imguiBlendCount,
                PAttachments = UseDynamicRenderingRenderTargets ? blendSlots : &colorAttachment
            };

            DynamicState* dynamicStates = stackalloc DynamicState[2];
            dynamicStates[0] = DynamicState.Viewport;
            dynamicStates[1] = DynamicState.Scissor;

            PipelineDynamicStateCreateInfo dynamicState = new()
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates
            };

            GraphicsPipelineCreateInfo pipelineInfo = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlendState,
                PDynamicState = &dynamicState,
                Layout = _imguiResources.PipelineLayout,
                RenderPass = UseDynamicRenderingRenderTargets ? default : _renderPass,
                Subpass = 0,
            };

            if (UseDynamicRenderingRenderTargets)
            {
                Format* colorFormats = stackalloc Format[(int)kDynRenderColorSlots];
                colorFormats[0] = swapChainImageFormat;

                PipelineRenderingCreateInfo renderingInfo = new()
                {
                    SType = StructureType.PipelineRenderingCreateInfo,
                    ColorAttachmentCount = kDynRenderColorSlots,
                    PColorAttachmentFormats = colorFormats,
                };

                pipelineInfo.PNext = &renderingInfo;
            }

            PipelineCreateFlags2CreateInfoNative imguiHeapFlags2 = default;
            if (IsDescriptorHeapDrawBindingActive)
            {
                imguiHeapFlags2 = new PipelineCreateFlags2CreateInfoNative
                {
                    SType = VulkanDescriptorHeapExt.PipelineCreateFlags2CreateInfoSType,
                    PNext = pipelineInfo.PNext,
                    Flags = unchecked((ulong)pipelineInfo.Flags) | VulkanDescriptorHeapExt.PipelineCreate2DescriptorHeapBit,
                };
                pipelineInfo.PNext = &imguiHeapFlags2;
            }

            if (Api.CreateGraphicsPipelines(device, default, 1, ref pipelineInfo, null, out _imguiResources.Pipeline) != Result.Success)
                throw new InvalidOperationException("Failed to create ImGui graphics pipeline.");

            RegisterVulkanPipeline(_imguiResources.Pipeline, "ImGui.Pipeline");
        }
        finally
        {
            Silk.NET.Core.Native.SilkMarshal.Free((nint)stages[0].PName);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)stages[1].PName);
        }
    }

    private int EnsureImGuiDrawBufferSlot(uint imageIndex)
    {
        int requiredSlots = Math.Max(MAX_FRAMES_IN_FLIGHT, swapChainImages?.Length ?? 0);
        if (imageIndex >= (uint)requiredSlots)
            requiredSlots = (int)imageIndex + 1;
        if (requiredSlots <= 0)
            requiredSlots = 1;

        if (_imguiResources.DrawBuffers.Length < requiredSlots)
            Array.Resize(ref _imguiResources.DrawBuffers, requiredSlots);

        return imageIndex < (uint)_imguiResources.DrawBuffers.Length ? (int)imageIndex : 0;
    }

    private int EnsureImGuiDrawBuffers(uint imageIndex, ulong vertexBytes, ulong indexBytes)
    {
        int bufferSlot = EnsureImGuiDrawBufferSlot(imageIndex);
        ref VulkanImGuiDrawBufferSet buffers = ref _imguiResources.DrawBuffers[bufferSlot];

        ulong requiredVertexBytes = Math.Max(vertexBytes, 1UL);
        ulong requiredIndexBytes = Math.Max(indexBytes, 1UL);

        if (buffers.VertexBuffer.Handle == 0 || buffers.VertexBufferSize < requiredVertexBytes)
        {
            if (buffers.VertexBuffer.Handle != 0)
                RetireBuffer(buffers.VertexBuffer, buffers.VertexBufferMemory);

            ulong capacity = ComputeImGuiBufferCapacity(buffers.VertexBufferSize, requiredVertexBytes);
            (buffers.VertexBuffer, buffers.VertexBufferMemory) = CreateBufferRaw(
                capacity,
                BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            buffers.VertexBufferSize = capacity;
        }

        if (buffers.IndexBuffer.Handle == 0 || buffers.IndexBufferSize < requiredIndexBytes)
        {
            if (buffers.IndexBuffer.Handle != 0)
                RetireBuffer(buffers.IndexBuffer, buffers.IndexBufferMemory);

            ulong capacity = ComputeImGuiBufferCapacity(buffers.IndexBufferSize, requiredIndexBytes);
            (buffers.IndexBuffer, buffers.IndexBufferMemory) = CreateBufferRaw(
                capacity,
                BufferUsageFlags.IndexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            buffers.IndexBufferSize = capacity;
        }

        return bufferSlot;
    }

    private static ulong ComputeImGuiBufferCapacity(ulong currentCapacity, ulong requiredBytes)
    {
        const ulong MinimumCapacity = 64UL * 1024UL;
        ulong target = Math.Max(requiredBytes, MinimumCapacity);

        if (currentCapacity > 0)
            target = Math.Max(target, currentCapacity <= ulong.MaxValue / 2UL ? currentCapacity * 2UL : ulong.MaxValue);

        return AlignUpToPowerOfTwoBucket(target);
    }

    private static ulong AlignUpToPowerOfTwoBucket(ulong value)
    {
        if (value <= 1UL)
            return 1UL;

        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        value |= value >> 32;
        return value == ulong.MaxValue ? ulong.MaxValue : value + 1UL;
    }

    private static void CopyImGuiSnapshot(VulkanImGuiFrameSnapshot snapshot, void* vertexDst, void* indexDst)
    {
        byte* vertexWritePtr = (byte*)vertexDst;
        byte* indexWritePtr = (byte*)indexDst;

        for (int listIndex = 0; listIndex < snapshot.CommandListCount; listIndex++)
        {
            VulkanImGuiCommandListSnapshot cmdList = snapshot.CommandLists[listIndex];

            nuint vertexBytes = (nuint)(cmdList.VertexCount * sizeof(ImDrawVert));
            nuint indexBytes = (nuint)(cmdList.IndexCount * sizeof(ushort));

            fixed (ImDrawVert* verticesPtr = cmdList.Vertices)
                System.Buffer.MemoryCopy(verticesPtr, vertexWritePtr, (long)vertexBytes, (long)vertexBytes);
            fixed (ushort* indicesPtr = cmdList.Indices)
                System.Buffer.MemoryCopy(indicesPtr, indexWritePtr, (long)indexBytes, (long)indexBytes);

            vertexWritePtr += (int)vertexBytes;
            indexWritePtr += (int)indexBytes;
        }
    }

}
