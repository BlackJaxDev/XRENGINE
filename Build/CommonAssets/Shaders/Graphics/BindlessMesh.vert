#version 460 core
#extension GL_ARB_bindless_texture : require

layout(location=0) in vec3 inPos;
layout(location=1) in vec3 inNormal;
layout(location=2) in vec2 inUV;

layout(std140, binding=0) uniform CameraBlock { mat4 uViewProj; };

// Per-draw data comes from indirect command buffer; we fetch model matrix from SSBO of full commands.
layout(std430, binding=10) buffer CommandsBuffer { float Commands[]; }; // 48 floats per command
const int COMMAND_FLOATS = 48;

out VS_OUT { vec3 N; vec2 UV; flat uint DrawID; } vs_out;

void main(){
    uint drawID = gl_DrawID; // requires multi-draw
    int base = int(drawID) * COMMAND_FLOATS;
    // WorldMatrix at offset 0-15
    mat4 model = mat4(
        vec4(Commands[base+0], Commands[base+1], Commands[base+2], Commands[base+3]),
        vec4(Commands[base+4], Commands[base+5], Commands[base+6], Commands[base+7]),
        vec4(Commands[base+8], Commands[base+9], Commands[base+10], Commands[base+11]),
        vec4(Commands[base+12], Commands[base+13], Commands[base+14], Commands[base+15])
    );
    // PrevWorldMatrix at offset 16-31 (available for motion vectors pass)
    gl_Position = uViewProj * model * vec4(inPos,1.0);
    vs_out.N = mat3(model) * inNormal;
    vs_out.UV = inUV;
    vs_out.DrawID = drawID;
}
