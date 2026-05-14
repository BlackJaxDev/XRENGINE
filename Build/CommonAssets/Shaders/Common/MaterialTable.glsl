#ifndef XR_MATERIAL_TABLE_GLSL
#define XR_MATERIAL_TABLE_GLSL

#extension GL_ARB_bindless_texture : require
#extension GL_ARB_gpu_shader_int64 : require

// XR generated material layout: DeferredOpaque.
// Layout must match MaterialBindingLayouts.OpaqueDeferred / GPUMaterialTable packing in C#.
// Row size: 48 bytes / 12 uint words.
struct XR_MaterialRecord {
    uint AlbedoHandleIndex;
    uint NormalHandleIndex;
    uint RMHandleIndex;
    uint Flags;
    vec4 BaseColorOpacity;
    vec4 RMSE;
};

layout(std430, binding = 11) readonly buffer XR_MaterialTableBuffer { XR_MaterialRecord XR_MaterialTable[]; };
#define MaterialEntry XR_MaterialRecord
#define MaterialTable XR_MaterialTable

bool XR_TryLoadMaterial(uint materialId, out XR_MaterialRecord material) {
    if (materialId >= uint(XR_MaterialTable.length()))
        return false;
    material = XR_MaterialTable[materialId];
    return true;
}

void XR_LoadMaterial(uint materialId, out XR_MaterialRecord material) {
    if (!XR_TryLoadMaterial(materialId, material))
        material = XR_MaterialRecord(0u, 0u, 0u, 0u, vec4(1.0, 1.0, 1.0, 1.0), vec4(1.0, 0.0, 1.0, 0.0));
}

struct TextureHandleEntry {
    uvec2 Handle; // low, high 32 bits
    uint Flags;
    uint Pad0;
};

layout(std430, binding = 17) readonly buffer XR_MaterialTextureHandleTableBuffer { TextureHandleEntry XR_TextureHandleTable[]; };
#define TextureHandleTable XR_TextureHandleTable

uint64_t XR_CombineHandle(uvec2 parts) {
    return (uint64_t(parts.y) << 32) | uint64_t(parts.x);
}

vec4 XR_TEXTURE2D(uint handleIndex, vec2 uv, vec4 fallback) {
    if (handleIndex == 0u || handleIndex >= uint(XR_TextureHandleTable.length()))
        return fallback;
    TextureHandleEntry entry = XR_TextureHandleTable[handleIndex];
    if ((entry.Flags & 1u) == 0u)
        return fallback;
    return texture(sampler2D(XR_CombineHandle(entry.Handle)), uv);
}

vec4 SampleBindlessTexture(uint handleIndex, vec2 uv, vec4 fallback) {
    return XR_TEXTURE2D(handleIndex, uv, fallback);
}

#endif // XR_MATERIAL_TABLE_GLSL
