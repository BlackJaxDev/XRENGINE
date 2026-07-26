using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Provides constants, structure types, and flags for the VK_EXT_descriptor_heap Vulkan extension.
/// </summary>
internal static class VulkanDescriptorHeapExt
{
    /// <summary>
    /// The name of the VK_EXT_descriptor_heap Vulkan extension.
    /// </summary>
    public const string ExtensionName = "VK_EXT_descriptor_heap";
    /// <summary>
    /// The name of the VK_KHR_shader_untyped_pointers Vulkan extension.
    /// </summary>
    public const string ShaderUntypedPointersExtensionName = "VK_KHR_shader_untyped_pointers";

    /// <summary>
    /// The base value for structure type enumerations associated with the VK_EXT_descriptor_heap extension.
    /// </summary>
    private const int ExtensionStructureTypeBase = 1000135000;

    /// <summary>
    /// The structure type for the texel buffer descriptor info structure.
    /// </summary>
    public static readonly StructureType TexelBufferDescriptorInfoSType 
        = (StructureType)(ExtensionStructureTypeBase + 0);
    /// <summary>
    /// The structure type for the image descriptor info structure.
    /// </summary>
    public static readonly StructureType ImageDescriptorInfoSType 
        = (StructureType)(ExtensionStructureTypeBase + 1);
    /// <summary>
    /// The structure type for the resource descriptor info structure.
    /// </summary>
    public static readonly StructureType ResourceDescriptorInfoSType 
        = (StructureType)(ExtensionStructureTypeBase + 2);
    /// <summary>
    /// The structure type for the bind heap info structure.
    /// </summary>
    public static readonly StructureType BindHeapInfoSType 
        = (StructureType)(ExtensionStructureTypeBase + 3);
    /// <summary>
    /// The structure type for the push data info structure.
    /// </summary>
    public static readonly StructureType PushDataInfoSType 
        = (StructureType)(ExtensionStructureTypeBase + 4);
    /// <summary>
    /// The structure type for the descriptor set and binding mapping structure.
    /// </summary>
    public static readonly StructureType DescriptorSetAndBindingMappingSType 
        = (StructureType)(ExtensionStructureTypeBase + 5);
    /// <summary>
    /// The structure type for the shader descriptor set and binding mapping info structure.
    /// </summary>
    public static readonly StructureType ShaderDescriptorSetAndBindingMappingInfoSType 
        = (StructureType)(ExtensionStructureTypeBase + 6);
    /// <summary>
    /// The structure type for the physical device descriptor heap properties structure.
    /// </summary>
    public static readonly StructureType PhysicalDeviceDescriptorHeapPropertiesSType 
        = (StructureType)(ExtensionStructureTypeBase + 8);
    /// <summary>
    /// The structure type for the physical device descriptor heap features structure.
    /// </summary>
    public static readonly StructureType PhysicalDeviceDescriptorHeapFeaturesSType 
        = (StructureType)(ExtensionStructureTypeBase + 9);
    /// <summary>
    /// The structure type for the command buffer inheritance descriptor heap info structure.
    /// </summary>
    public static readonly StructureType CommandBufferInheritanceDescriptorHeapInfoSType 
        = (StructureType)(ExtensionStructureTypeBase + 10);
    /// <summary>
    /// The structure type for the pipeline create flags 2 create info structure.
    /// </summary>
    public static readonly StructureType PipelineCreateFlags2CreateInfoSType 
        = (StructureType)1000470005;

    /// <summary>
    /// The usage flags for the descriptor heap buffer.
    /// </summary>
    public const BufferUsageFlags DescriptorHeapBufferUsage = (BufferUsageFlags)(1u << 28);
    /// <summary>
    /// The pipeline create flags for the descriptor heap.
    /// </summary>
    public const ulong PipelineCreate2DescriptorHeapBit = 1ul << 36;
    /// <summary>
    /// The pipeline create flags for the sampler heap read access.
    /// </summary>
    public const ulong SamplerHeapReadAccess2 = 1ul << 57;
    /// <summary>
    /// The pipeline create flags for the resource heap read access.
    /// </summary>
    public const ulong ResourceHeapReadAccess2 = 1ul << 58;
}
