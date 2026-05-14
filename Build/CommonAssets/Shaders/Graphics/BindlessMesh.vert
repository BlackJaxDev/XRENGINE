#version 460 core
#extension GL_ARB_bindless_texture : require

layout(location=0) in vec3 inPos;
layout(location=1) in vec3 inNormal;
layout(location=2) in vec2 inUV;

layout(std140, binding=0) uniform CameraBlock { mat4 uViewProj; };

struct DrawMetadata
{
    uint DrawID;
    uint MeshID;
    uint SubmeshID;
    uint MaterialID;
    uint TransformID;
    uint SkinID;
    uint RenderPassMask;
    uint LayerMask;
    uint Flags;
    uint LodPolicy;
    uint StateClassID;
    uint InstanceCount;
    uint RenderPass;
    uint ShaderProgramID;
    uint LogicalMeshID;
    uint BoundsID;
};

layout(std430, binding=9) readonly buffer TransformBuffer { float Transforms[]; };
layout(std430, binding=10) readonly buffer DrawMetadataBuffer { DrawMetadata Draws[]; };

const uint MATRIX_FLOATS = 16u;

out VS_OUT { vec3 N; vec2 UV; flat uint DrawID; } vs_out;

mat4 LoadWorldMatrix(uint transformID)
{
    uint base = transformID * MATRIX_FLOATS;
    if (base + 15u >= uint(Transforms.length()))
        return mat4(1.0);

    return mat4(
        vec4(Transforms[base+0u],  Transforms[base+1u],  Transforms[base+2u],  Transforms[base+3u]),
        vec4(Transforms[base+4u],  Transforms[base+5u],  Transforms[base+6u],  Transforms[base+7u]),
        vec4(Transforms[base+8u],  Transforms[base+9u],  Transforms[base+10u], Transforms[base+11u]),
        vec4(Transforms[base+12u], Transforms[base+13u], Transforms[base+14u], Transforms[base+15u])
    );
}

void main(){
    uint drawID = gl_DrawID;
    uint transformID = drawID < uint(Draws.length()) ? Draws[drawID].TransformID : 0u;
    mat4 model = LoadWorldMatrix(transformID);

    gl_Position = uViewProj * model * vec4(inPos,1.0);
    vs_out.N = mat3(model) * inNormal;
    vs_out.UV = inUV;
    vs_out.DrawID = drawID;
}
