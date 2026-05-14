#ifndef XR_VULKAN_BINDLESS_MATERIAL_TABLE_GLSL
#define XR_VULKAN_BINDLESS_MATERIAL_TABLE_GLSL

#extension GL_EXT_nonuniform_qualifier : require

// Vulkan descriptor-indexing material path.
// The set/binding values are mirrored by VulkanBindlessMaterialDescriptors.cs.
layout(set = 2, binding = 31) uniform sampler2D XR_BindlessMaterialTextures[];

vec4 XR_SampleBindlessMaterialTexture(uint descriptorIndex, vec2 uv, vec4 fallback)
{
    if (descriptorIndex == 0u)
        return fallback;

    return texture(XR_BindlessMaterialTextures[nonuniformEXT(descriptorIndex)], uv);
}

#endif // XR_VULKAN_BINDLESS_MATERIAL_TABLE_GLSL
