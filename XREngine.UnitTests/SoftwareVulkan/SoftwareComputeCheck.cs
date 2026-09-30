using Silk.NET.Vulkan;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.UnitTests.SoftwareVulkan;

/// <summary>Validates shader compilation, compute dispatch, and storage-buffer visibility on the production host.</summary>
internal sealed unsafe class SoftwareComputeCheck
{
    private const uint ValueCount = 64;
    private const ulong BufferByteCount = ValueCount * sizeof(uint);
    private const string ShaderSource = """
        #version 450
        layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
        layout(set = 0, binding = 0, std430) writeonly buffer ResultBuffer
        {
            uint values[];
        } resultBuffer;
        void main()
        {
            uint index = gl_GlobalInvocationID.x;
            resultBuffer.values[index] = index * 3u + 7u;
        }
        """;

    private SoftwareComputeCheck()
    {
    }

    /// <summary>Submits one compute workload and checks every resulting word after native completion.</summary>
    public static void Run(VulkanExplicitTargetRendererHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        byte[] spirv = ShaderCrossCompiler.CompileGlslToSpirv(
            ShaderSource, EShaderType.Compute, "SoftwareVulkan.StorageBuffer.comp");
        if (spirv.Length == 0 || spirv.Length % sizeof(uint) != 0)
            throw new InvalidOperationException("The software Vulkan compute shader compiler returned invalid SPIR-V length.");

        Vk api = host.Api;
        Device device = host.Device;
        Buffer buffer = default;
        DeviceMemory memory = default;
        ShaderModule shaderModule = default;
        DescriptorSetLayout descriptorLayout = default;
        DescriptorPool descriptorPool = default;
        PipelineLayout pipelineLayout = default;
        Pipeline pipeline = default;
        bool submissionAttempted = false;
        bool completed = false;

        try
        {
            BufferCreateInfo bufferInfo = new()
            {
                SType = StructureType.BufferCreateInfo,
                Size = BufferByteCount,
                Usage = BufferUsageFlags.StorageBufferBit,
                SharingMode = SharingMode.Exclusive,
            };
            Check(api.CreateBuffer(device, in bufferInfo, null, out buffer), "create storage buffer");
            api.GetBufferMemoryRequirements(device, buffer, out MemoryRequirements requirements);
            uint memoryType = FindHostMemoryType(api, host.PhysicalDevice, requirements.MemoryTypeBits, out bool coherent);
            MemoryAllocateInfo allocationInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryType,
            };
            Check(api.AllocateMemory(device, in allocationInfo, null, out memory), "allocate storage-buffer memory");
            Check(api.BindBufferMemory(device, buffer, memory, 0), "bind storage-buffer memory");
            InitializeSentinel(api, device, memory, coherent);

            DescriptorSetLayoutBinding binding = new()
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
            DescriptorSetLayoutCreateInfo descriptorLayoutInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding,
            };
            Check(api.CreateDescriptorSetLayout(device, in descriptorLayoutInfo, null, out descriptorLayout), "create compute descriptor layout");

            DescriptorPoolSize poolSize = new(DescriptorType.StorageBuffer, 1);
            DescriptorPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
            };
            Check(api.CreateDescriptorPool(device, in poolInfo, null, out descriptorPool), "create compute descriptor pool");

            DescriptorSetLayout layout = descriptorLayout;
            DescriptorSetAllocateInfo setInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };
            Check(api.AllocateDescriptorSets(device, in setInfo, out DescriptorSet descriptorSet), "allocate compute descriptor set");
            DescriptorBufferInfo descriptorBuffer = new(buffer, 0, BufferByteCount);
            WriteDescriptorSet descriptorWrite = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &descriptorBuffer,
            };
            api.UpdateDescriptorSets(device, 1, in descriptorWrite, 0, null);

            PipelineLayoutCreateInfo pipelineLayoutInfo = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &layout,
            };
            Check(api.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out pipelineLayout), "create compute pipeline layout");
            fixed (byte* code = spirv)
            {
                ShaderModuleCreateInfo shaderInfo = new()
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                    PCode = (uint*)code,
                };
                Check(api.CreateShaderModule(device, in shaderInfo, null, out shaderModule), "create compute shader module");
            }

            fixed (byte* entryPoint = "main\0"u8)
            {
                ComputePipelineCreateInfo pipelineInfo = new()
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.ComputeBit,
                        Module = shaderModule,
                        PName = entryPoint,
                    },
                    Layout = pipelineLayout,
                    BasePipelineIndex = -1,
                };
                Check(api.CreateComputePipelines(device, default, 1, in pipelineInfo, null, out pipeline), "create compute pipeline");
            }

            // This cold validation callback owns no renderer and submits only through the production host.
            submissionAttempted = true;
            host.SubmitFrame((frameApi, commands, target) =>
                Record(frameApi, commands, target, pipeline, pipelineLayout, descriptorSet, buffer));
            // SubmitFrame may return before its frame-slot fence signals. Never map or retire in-flight storage.
            Check(api.DeviceWaitIdle(device), "wait for compute completion");
            completed = true;
            VerifyReadback(api, device, memory, coherent);
        }
        finally
        {
            // Submission can be accepted before host bookkeeping throws. Settle it before native teardown.
            bool mayDestroy = !submissionAttempted || completed;
            if (!mayDestroy)
            {
                Result waitResult = api.DeviceWaitIdle(device);
                mayDestroy = waitResult is Result.Success or Result.ErrorDeviceLost;
            }

            // If a non-device-loss wait fails, host/device teardown owns the remaining allocations;
            // destroying potentially in-flight objects here would make the original failure unsafe.
            if (mayDestroy)
            {
                if (pipeline.Handle != 0)
                    api.DestroyPipeline(device, pipeline, null);
                if (pipelineLayout.Handle != 0)
                    api.DestroyPipelineLayout(device, pipelineLayout, null);
                if (descriptorPool.Handle != 0)
                    api.DestroyDescriptorPool(device, descriptorPool, null);
                if (descriptorLayout.Handle != 0)
                    api.DestroyDescriptorSetLayout(device, descriptorLayout, null);
                if (shaderModule.Handle != 0)
                    api.DestroyShaderModule(device, shaderModule, null);
                if (buffer.Handle != 0)
                    api.DestroyBuffer(device, buffer, null);
                if (memory.Handle != 0)
                    api.FreeMemory(device, memory, null);
            }
        }
    }

    private static uint FindHostMemoryType(Vk api, PhysicalDevice physicalDevice, uint allowedTypes, out bool coherent)
    {
        api.GetPhysicalDeviceMemoryProperties(physicalDevice, out PhysicalDeviceMemoryProperties properties);
        uint fallback = uint.MaxValue;
        for (uint index = 0; index < properties.MemoryTypeCount; ++index)
        {
            MemoryPropertyFlags flags = properties.MemoryTypes[(int)index].PropertyFlags;
            if ((allowedTypes & (1u << (int)index)) == 0 || (flags & MemoryPropertyFlags.HostVisibleBit) == 0)
                continue;
            if ((flags & MemoryPropertyFlags.HostCoherentBit) != 0)
            {
                coherent = true;
                return index;
            }
            fallback = index;
        }
        coherent = false;
        if (fallback != uint.MaxValue)
            return fallback;
        throw new InvalidOperationException("The software Vulkan compute storage buffer has no host-visible memory type.");
    }

    private static void InitializeSentinel(Vk api, Device device, DeviceMemory memory, bool coherent)
    {
        void* mapped = null;
        Check(api.MapMemory(device, memory, 0, Vk.WholeSize, 0, &mapped), "map compute sentinel storage");
        try
        {
            new Span<uint>(mapped, (int)ValueCount).Fill(uint.MaxValue);
            if (!coherent)
            {
                MappedMemoryRange range = new()
                {
                    SType = StructureType.MappedMemoryRange,
                    Memory = memory,
                    Offset = 0,
                    Size = Vk.WholeSize,
                };
                Check(api.FlushMappedMemoryRanges(device, 1, in range), "flush compute sentinel storage");
            }
        }
        finally
        {
            api.UnmapMemory(device, memory);
        }
    }

    private static void Record(Vk api, CommandBuffer commands, VulkanRenderFrameTarget target,
        Pipeline pipeline, PipelineLayout layout, DescriptorSet descriptorSet, Buffer buffer)
    {
        api.CmdBindPipeline(commands, PipelineBindPoint.Compute, pipeline);
        api.CmdBindDescriptorSets(commands, PipelineBindPoint.Compute, layout, 0, 1, &descriptorSet, 0, null);
        api.CmdDispatch(commands, 1, 1, 1);

        BufferMemoryBarrier hostRead = new()
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.HostReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = buffer,
            Offset = 0,
            Size = BufferByteCount,
        };
        api.CmdPipelineBarrier(commands, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit,
            0, 0, null, 1, &hostRead, 0, null);

        // Even a buffer-only frame must fulfill the acquired output's final-layout contract.
        if (target.InitialColorLayout == target.RequiredFinalColorLayout)
            return;
        ImageMemoryBarrier finalLayout = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = target.InitialColorLayout,
            NewLayout = target.RequiredFinalColorLayout,
            SrcAccessMask = target.InitialColorLayout == ImageLayout.Undefined
                ? 0 : AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = target.ColorImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, target.Layers),
        };
        api.CmdPipelineBarrier(commands, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &finalLayout);
    }

    private static void VerifyReadback(Vk api, Device device, DeviceMemory memory, bool coherent)
    {
        void* mapped = null;
        Check(api.MapMemory(device, memory, 0, Vk.WholeSize, 0, &mapped), "map compute results");
        try
        {
            if (!coherent)
            {
                MappedMemoryRange range = new()
                {
                    SType = StructureType.MappedMemoryRange,
                    Memory = memory,
                    Offset = 0,
                    Size = Vk.WholeSize,
                };
                Check(api.InvalidateMappedMemoryRanges(device, 1, in range), "invalidate compute readback memory");
            }
            ReadOnlySpan<uint> values = new(mapped, (int)ValueCount);
            for (int index = 0; index < values.Length; ++index)
            {
                uint expected = (uint)index * 3u + 7u;
                if (values[index] != expected)
                {
                    throw new InvalidOperationException(
                        $"Software Vulkan compute mismatch at index {index}: expected {expected}, received {values[index]}.");
                }
            }
        }
        finally
        {
            api.UnmapMemory(device, memory);
        }
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}.");
    }
}
