#ifndef XR_MATERIAL_TABLE_GLSL
#define XR_MATERIAL_TABLE_GLSL

// Layout must match GPUMaterialEntry packing in C# (48 bytes / 12 uints per entry)
struct MaterialEntry {
    uvec2 AlbedoHandle;   // low, high 32 bits
    uvec2 NormalHandle;   // low, high
    uvec2 RMHandle;       // low, high
    uint Flags;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

layout(std430, binding = 9) buffer MaterialTableBuffer { MaterialEntry MaterialTable[]; };

// Fetch 64-bit bindless sampler handle (ARB_bindless_texture) reconstructed from two uints.
uint64_t XR_CombineHandle(uvec2 parts) {
    return (uint64_t(parts.y) << 32) | uint64_t(parts.x);
}

#endif // XR_MATERIAL_TABLE_GLSL
