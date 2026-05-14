#version 460 core
#extension GL_ARB_bindless_texture : require
#extension GL_ARB_gpu_shader_int64 : require

// Inlined material table definitions (no GLSL include extension assumed)
struct MaterialEntry {
    uint AlbedoHandleIndex;
    uint NormalHandleIndex;
    uint RMHandleIndex;
    uint Flags;
};
layout(std430, binding = 11) readonly buffer MaterialTableBuffer { MaterialEntry MaterialTable[]; };
struct TextureHandleEntry {
    uvec2 Handle;
    uint Flags;
    uint Pad0;
};
layout(std430, binding = 17) readonly buffer MaterialTextureHandleTableBuffer { TextureHandleEntry TextureHandleTable[]; };
#extension GL_ARB_gpu_shader_int64 : enable
uint64_t XR_CombineHandle(uvec2 parts){ return (uint64_t(parts.y) << 32) | uint64_t(parts.x); }

in VS_OUT { vec3 N; vec2 UV; flat uint DrawID; } fs_in;
layout(location=0) out vec4 outColor;

// Commands buffer (to read MaterialID at float slot 22)
layout(std430, binding=12) readonly buffer DrawMetadataBuffer { uint DrawMetadataWords[]; };
const int DRAW_METADATA_WORDS = 16;
const int DRAW_METADATA_MATERIAL_ID_WORD = 3;

vec4 SampleBindless(uint handleIndex, vec2 uv, vec4 fallback){
    if(handleIndex == 0u || handleIndex >= uint(TextureHandleTable.length()))
        return fallback;
    TextureHandleEntry entry = TextureHandleTable[handleIndex];
    if((entry.Flags & 1u) == 0u)
        return fallback;
    return texture(sampler2D(XR_CombineHandle(entry.Handle)), uv);
}

void main(){
    uint drawID = fs_in.DrawID;
    int base = int(drawID) * DRAW_METADATA_WORDS;
    uint materialID = DrawMetadataWords[base + DRAW_METADATA_MATERIAL_ID_WORD];
    if(materialID >= MaterialTable.length()) { outColor = vec4(1,0,1,1); return; }
    MaterialEntry m = MaterialTable[materialID];
    vec4 fallback = vec4(1,0,1,1);
    vec4 albedo = SampleBindless(m.AlbedoHandleIndex, fs_in.UV, fallback);
    vec3 N = normalize(fs_in.N);
    float ndotl = clamp(dot(N, normalize(vec3(0.4,0.8,0.6))), 0.0, 1.0);
    outColor = vec4(albedo.rgb * (0.2 + 0.8*ndotl), albedo.a);
}
