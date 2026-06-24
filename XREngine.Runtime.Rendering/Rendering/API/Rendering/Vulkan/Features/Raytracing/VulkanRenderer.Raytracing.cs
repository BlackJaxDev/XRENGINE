using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Graphics.Renderers.Vulkan
{
    //public partial class VulkanRenderer : AbstractRenderer<Vk>
    //{
    //    private const string EXT_KHR_AccelerationStructure = "VK_KHR_acceleration_structure";

    //    private KhrAccelerationStructure? accelStruct;
    //    public KhrAccelerationStructure? AccelStruct
    //    {
    //        get
    //        {
    //            VerifyExt(EXT_KHR_AccelerationStructure, ref accelStruct);
    //            return accelStruct;
    //        }
    //    }

    //    protected override bool LoadExt<T>(out T output)
    //        => API!.TryGetInstanceExtension(instance, out output);

    //    public unsafe void InitBottomLevel()
    //    {
    //        AccelerationStructureCreateInfoKHR accelerationStructureCreateInfo = new AccelerationStructureCreateInfoKHR
    //        {
    //            SType = StructureType.AccelerationStructureCreateInfoKhr,
    //            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
    //        };

    //        AccelerationStructureKHR bottomLevelAccelerationStructure;
    //        AccelStruct?.CreateAccelerationStructure(device, &accelerationStructureCreateInfo, null, &bottomLevelAccelerationStructure);

    //         Allocate memory for the acceleration structure
    //        MemoryRequirements memoryRequirements;
    //        API!.GetAccelerationStructureMemoryRequirementsKHR(logicalDevice, new AccelerationStructureMemoryRequirementsInfoKhr
    //        {
    //            sType = StructureType.AccelerationStructureMemoryRequirementsInfoKHR,
    //            accelerationStructure = bottomLevelAccelerationStructure
    //        }, &memoryRequirements);

    //         TODO: Allocate and bind memory for the acceleration structure
    //         ...

    //         Create geometry data for the bottom-level acceleration structure
    //        VkAccelerationStructureGeometryKHR geometry = new VkAccelerationStructureGeometryKHR
    //        {
    //            sType = VkStructureType.StructureTypeAccelerationStructureGeometryKHR,
    //            geometryType = VkGeometryTypeKHR.GeometryTypeTrianglesKHR,
    //            geometry = new VkAccelerationStructureGeometryDataKHR
    //            {
    //                triangles = new VkAccelerationStructureGeometryTrianglesDataKHR
    //                {
    //                    sType = VkStructureType.StructureTypeAccelerationStructureGeometryTrianglesDataKHR,
    //                    vertexFormat = VkFormat.FormatR32g32b32Sfloat,
    //                    vertexData = vertexBufferDeviceAddress,
    //                    vertexStride = (ulong)Marshal.SizeOf<Vertex>(),
    //                    indexType = VkIndexType.IndexTypeNoneKHR,
    //                    transformData = 0
    //                }
    //            }
    //        };

    //         Create build range info for the acceleration structure
    //        AccelerationStructureBuildRangeInfoKHR buildRangeInfo = new AccelerationStructureBuildRangeInfoKHR
    //        {
    //            primitiveCount = 1,
    //            primitiveOffset = 0,
    //            firstVertex = 0,
    //            transformedVertexStride = 0
    //        };

    //         Build the bottom-level acceleration structure
    //        AccelerationStructureBuildGeometryInfoKHR buildGeometryInfo = new AccelerationStructureBuildGeometryInfoKHR
    //        {
    //            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
    //            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
    //            Flags = VkBuildAccelerationStructureFlagsKHR.BuildAccelerationStructurePreferFastTraceKHR,
    //            GeometryCount = 1,
    //            PGeometries = &geometry,
    //            SrcAccelerationStructure = AccelerationStructureKHR.Null,
    //            DstAccelerationStructure = bottomLevelAccelerationStructure
    //        };

    //         Check if the device supports the required acceleration structure build sizes
    //        VkAccelerationStructureBuildSizesInfoKHR buildSizesInfo = new VkAccelerationStructureBuildSizesInfoKHR
    //        {
    //            sType = VkStructureType.StructureTypeAccelerationStructureBuildSizesInfoKHR
    //        };
    //        vkGetAccelerationStructureBuildSizesKHR(logicalDevice, VkAccelerationStructureBuildTypeKHR.AccelerationStructureBuildTypeDeviceKHR, &buildGeometryInfo, &buildRangeInfo.primitiveCount, &buildSizesInfo);

    //         TODO: Allocate memory for the scratch buffer based on buildSizesInfo
    //         ...

    //         Set the buildGeometryInfo.scratchDataDeviceAddress
    //        buildGeometryInfo.scratchDataDeviceAddress = scratchBufferDeviceAddress;

    //         Build the acceleration structure
    //        VkCommandBuffer commandBuffer = BeginSingleTimeCommands(logicalDevice, commandPool);
    //        vkCmdBuildAccelerationStructuresKHR(commandBuffer, 1, &buildGeometryInfo, &buildRangeInfo);
    //        EndSingleTimeCommands(logicalDevice, commandPool, queue, commandBuffer);

    //         TODO: Clean up resources, such as memory allocations and buffers
    //         ...

    //         After creating the bottom-level acceleration structure, you can proceed
    //         to create a top-level acceleration structure, set up the shader binding
    //         table (SBT), and perform ray tracing.
    //    }

    //    public unsafe void InitTopLevel()
    //    {
    //         Create a single instance with the bottom-level acceleration structure
    //        Instance instance = new Instance
    //        {
    //            Transform = Matrix4x4.Identity,
    //            InstanceCustomIndex = 0,
    //            Mask = 0xFF,
    //            InstanceShaderBindingTableRecordOffset = 0,
    //            Flags = VkGeometryInstanceFlagsKHR.GeometryInstanceTriangleFacingCullDisableKHR,
    //            AccelerationStructureDeviceAddress = GetAccelerationStructureDeviceAddress(logicalDevice, bottomLevelAccelerationStructure)
    //        };

    //         Create a buffer to store instance data
    //        VkBuffer instanceBuffer;
    //        VkDeviceMemory instanceBufferMemory;
    //        ulong instanceBufferDeviceAddress;
    //        CreateBuffer(logicalDevice, physicalDevice, (ulong)Marshal.SizeOf<Instance>(), VkBufferUsageFlags.BufferUsageShaderDeviceAddressKHR | VkBufferUsageFlags.BufferUsageTransferDstKHR, VkMemoryPropertyFlags.MemoryPropertyDeviceLocalBit, out instanceBuffer, out instanceBufferMemory);
    //        instanceBufferDeviceAddress = GetBufferDeviceAddress(logicalDevice, instanceBuffer);

    //         Copy instance data to the instance buffer
    //        CopyDataToBuffer(logicalDevice, commandPool, queue, instanceBufferMemory, new Instance[] { instance });

    //         Create top-level acceleration structure
    //        VkAccelerationStructureCreateInfoKHR topLevelAccelerationStructureCreateInfo = new VkAccelerationStructureCreateInfoKHR
    //        {
    //            sType = VkStructureType.StructureTypeAccelerationStructureCreateInfoKHR,
    //            type = VkAccelerationStructureTypeKHR.AccelerationStructureTypeTopLevelKHR,
    //            maxInstanceCount = 1
    //        };

    //        VkAccelerationStructureKHR topLevelAccelerationStructure;
    //        vkCreateAccelerationStructureKHR(logicalDevice, &topLevelAccelerationStructureCreateInfo, null, &topLevelAccelerationStructure);

    //         Allocate memory for the top-level acceleration structure
    //        VkMemoryRequirements topLevelMemoryRequirements;
    //        vkGetAccelerationStructureMemoryRequirementsKHR(logicalDevice, new VkAccelerationStructureMemoryRequirementsInfoKHR
    //        {
    //            sType = VkStructureType.StructureTypeAccelerationStructureMemoryRequirementsInfoKHR,
    //            accelerationStructure = topLevelAccelerationStructure
    //        }, &topLevelMemoryRequirements);

    //         TODO: Allocate and bind memory for the top-level acceleration structure
    //         ...

    //         Build the top-level acceleration structure
    //        VkAccelerationStructureGeometryKHR topLevelGeometry = new VkAccelerationStructureGeometryKHR
    //        {
    //            sType = VkStructureType.StructureTypeAccelerationStructureGeometryKHR,
    //            geometryType = VkGeometryTypeKHR.GeometryTypeInstancesKHR,
    //            geometry = new VkAccelerationStructureGeometryDataKHR
    //            {
    //                instances = new VkAccelerationStructureGeometryInstancesDataKHR
    //                {
    //                    sType = VkStructureType.StructureTypeAccelerationStructureGeometryInstancesDataKHR,
    //                    arrayOfPointers = VkBool32.False,
    //                    data = instanceBufferDeviceAddress
    //                }
    //            }
    //        };

    //        VkAccelerationStructureBuildRangeInfoKHR topLevelBuildRangeInfo = new VkAccelerationStructureBuildRangeInfoKHR
    //        {
    //            primitiveCount = 1,
    //            primitiveOffset = 0,
    //            firstVertex = 0,
    //            transformedVertexStride = 0
    //        };

    //        VkAccelerationStructureBuildGeometryInfoKHR topLevelBuildGeometryInfo = new VkAccelerationStructureBuildGeometryInfoKHR
    //        {
    //            sType = VkStructureType.StructureTypeAccelerationStructureBuildGeometryInfoKHR,
    //            type = VkAccelerationStructureTypeKHR.AccelerationStructureTypeTopLevelKHR,
    //            flags = VkBuildAccelerationStructureFlagsKHR.BuildAccelerationStructurePreferFastTraceKHR,
    //            geometryCount = 1,
    //            pGeometries = &topLevelGeometry,
    //            srcAccelerationStructure = VkAccelerationStructureKHR.Null,
    //            dstAccelerationStructure = topLevelAccelerationStructure
    //        };

    //         Check if the device supports the required acceleration structure build sizes
    //        VkAccelerationStructureBuildSizesInfoKHR topLevelBuildSizesInfo = new VkAccelerationStructureBuildSizesInfoKHR
    //        {
    //            sType = VkStructureType.StructureTypeAccelerationStructureBuildSizesInfoKHR
    //        };
    //        vkGetAccelerationStructureBuildSizesKHR(logicalDevice, VkAccelerationStructureBuildTypeKHR.AccelerationStructureBuildTypeDeviceKHR, &topLevelBuildGeometryInfo, &topLevelBuildRangeInfo.primitiveCount, &topLevelBuildSizesInfo);

    //         TODO: Allocate memory for the scratch buffer based on topLevelBuildSizesInfo
    //         ...

    //         Set the topLevelBuildGeometryInfo.scratchDataDeviceAddress
    //        topLevelBuildGeometryInfo.scratchDataDeviceAddress = topLevelScratchBufferDeviceAddress;

    //         Build the acceleration structure
    //        VkCommandBuffer commandBuffer = BeginSingleTimeCommands(logicalDevice, commandPool);
    //        vkCmdBuildAccelerationStructuresKHR(commandBuffer, 1, &topLevelBuildGeometryInfo, &topLevelBuildRangeInfo);
    //        EndSingleTimeCommands(logicalDevice, commandPool, queue, commandBuffer);

    //         TODO: Clean up resources, such as memory allocations and buffers
    //         ...

    //         After creating the top-level acceleration structure, you can proceed
    //         to set up the shader binding table (SBT) and perform ray tracing.
    //    }
    //    private void CreateBuffer(VkDevice logicalDevice, VkPhysicalDevice physicalDevice, ulong size, VkBufferUsageFlags usage, VkMemoryPropertyFlags properties, out VkBuffer buffer, out VkDeviceMemory bufferMemory)
    //    {
    //        // Create buffer
    //        VkBufferCreateInfo bufferCreateInfo = new VkBufferCreateInfo
    //        {
    //            sType = VkStructureType.StructureTypeBufferCreateInfo,
    //            size = size,
    //            usage = usage,
    //            sharingMode = VkSharingMode.SharingModeExclusive
    //        };

    //        vkCreateBuffer(logicalDevice, &bufferCreateInfo, null, out buffer);

    //        // Allocate memory
    //        vkGetBufferMemoryRequirements(logicalDevice, buffer, out VkMemoryRequirements memoryRequirements);
    //        VkMemoryAllocateInfo allocInfo = new VkMemoryAllocateInfo
    //        {
    //            sType = VkStructureType.StructureTypeMemoryAllocateInfo,
    //            allocationSize = memoryRequirements.size,
    //            memoryTypeIndex = FindMemoryType(physicalDevice, memoryRequirements.memoryTypeBits, properties)
    //        };

    //        vkAllocateMemory(logicalDevice, &allocInfo, null, out bufferMemory);

    //        // Bind buffer and memory
    //        vkBindBufferMemory(logicalDevice, buffer, bufferMemory, 0);
    //    }

    //    private uint FindMemoryType(PhysicalDevice physicalDevice, uint typeFilter, VkMemoryPropertyFlags properties)
    //    {
    //        vkGetPhysicalDeviceMemoryProperties(physicalDevice, out VkPhysicalDeviceMemoryProperties memoryProperties);

    //        for (uint i = 0; i < memoryProperties.memoryTypeCount; i++)
    //        {
    //            if ((typeFilter & (1 << (int)i)) != 0 && (memoryProperties.memoryTypes[i].propertyFlags & properties) == properties)
    //            {
    //                return i;
    //            }
    //        }

    //        throw new Exception("Failed to find suitable memory type.");
    //    }
    //    public void Allocate()
    //    {
    //         Allocate and bind memory for the bottom-level acceleration structure
    //        AllocateAndBindAccelerationStructureMemory(logicalDevice, physicalDevice, bottomLevelAccelerationStructure, memoryRequirements);

    //         Allocate and bind memory for the top-level acceleration structure
    //        AllocateAndBindAccelerationStructureMemory(logicalDevice, physicalDevice, topLevelAccelerationStructure, topLevelMemoryRequirements);
    //    }
    //    private void AllocateAndBindAccelerationStructureMemory(Device logicalDevice, PhysicalDevice physicalDevice, AccelerationStructureKHR accelerationStructure, MemoryRequirements memoryRequirements)
    //    {
    //         Allocate memory
    //        MemoryAllocateInfo memoryAllocateInfo = new MemoryAllocateInfo
    //        {
    //            sType = StructureType.MemoryAllocateInfo,
    //            allocationSize = memoryRequirements.size,
    //            memoryTypeIndex = FindMemoryType(physicalDevice, memoryRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
    //        };

    //        DeviceMemory memory;
    //        vkAllocateMemory(logicalDevice, &memoryAllocateInfo, null, &memory);

    //         Bind memory and acceleration structure
    //        VkBindAccelerationStructureMemoryInfoKHR bindInfo = new VkBindAccelerationStructureMemoryInfoKHR
    //        {
    //            sType = StructureType.StructureTypeBindAccelerationStructureMemoryInfoKHR,
    //            accelerationStructure = accelerationStructure,
    //            memory = memory
    //        };

    //        vkBindAccelerationStructureMemoryKHR(logicalDevice, 1, &bindInfo);
    //    }

    //}
}
