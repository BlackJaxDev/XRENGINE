#version 460 core
#extension GL_ARB_bindless_texture : require
#extension GL_ARB_gpu_shader_int64 : require

// Inlined material table definitions (no GLSL include extension assumed)
struct MaterialEntry {
    uvec2 AlbedoHandle;
    uvec2 NormalHandle;
    uvec2 RMHandle;
    uint Flags; uint Pad0; uint Pad1; uint Pad2;
};
layout(std430, binding = 9) buffer MaterialTableBuffer { MaterialEntry MaterialTable[]; };
// Reconstruct sampler handle (ARB_bindless_texture exposes sampler2D with 64-bit handle via uint64ARB if supported).
#extension GL_ARB_gpu_shader_int64 : enable
uint64_t XR_CombineHandle(uvec2 parts){ return (uint64_t(parts.y) << 32) | uint64_t(parts.x); }

in VS_OUT { vec3 N; vec2 UV; flat uint DrawID; } fs_in;
layout(location=0) out vec4 outColor;

// Commands buffer (to read MaterialID at float slot 22)
layout(std430, binding=10) buffer CommandsBuffer { float Commands[]; };
const int COMMAND_FLOATS = 32;

// Fallback: if bindless sampler handle constructors unavailable in validator, return solid color placeholder.
vec4 SampleBindless(uint64_t handle, vec2 uv){
    // TODO: Replace with proper bindless sampling once driver/compiler supports handle constructors here.
    vec2 fuv = fract(uv);
    return vec4(fuv, 0.5, 1.0);
}

void main(){
    uint drawID = fs_in.DrawID;
    int base = int(drawID) * COMMAND_FLOATS;
    uint materialID = uint(Commands[base + 22]);
    if(materialID >= MaterialTable.length()) { outColor = vec4(1,0,1,1); return; }
    MaterialEntry m = MaterialTable[materialID];
    uint64_t albedoHandle = XR_CombineHandle(m.AlbedoHandle);
    vec4 albedo = SampleBindless(albedoHandle, fs_in.UV);
    vec3 N = normalize(fs_in.N);
    float ndotl = clamp(dot(N, normalize(vec3(0.4,0.8,0.6))), 0.0, 1.0);
    outColor = vec4(albedo.rgb * (0.2 + 0.8*ndotl), albedo.a);
}
