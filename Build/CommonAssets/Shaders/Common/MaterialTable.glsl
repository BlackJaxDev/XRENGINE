#ifndef XR_MATERIAL_TABLE_GLSL
#define XR_MATERIAL_TABLE_GLSL

// Layout must match GPUMaterialEntry packing in C# (16 bytes / 4 uints per entry).
// Material entries point at the second-level handle table; they do not store API handles directly.
struct MaterialEntry {
    uint AlbedoHandleIndex;
    uint NormalHandleIndex;
    uint RMHandleIndex;
    uint Flags;
};

layout(std430, binding = 11) readonly buffer MaterialTableBuffer { MaterialEntry MaterialTable[]; };

struct TextureHandleEntry {
    uvec2 Handle; // low, high 32 bits
    uint Flags;
    uint Pad0;
};

layout(std430, binding = 17) readonly buffer MaterialTextureHandleTableBuffer { TextureHandleEntry MaterialTextureHandles[]; };

uint64_t XR_CombineHandle(uvec2 parts) {
    return (uint64_t(parts.y) << 32) | uint64_t(parts.x);
}

#endif // XR_MATERIAL_TABLE_GLSL
