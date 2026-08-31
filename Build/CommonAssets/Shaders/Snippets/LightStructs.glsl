// LightStructs snippet
// Usage: #pragma snippet "LightStructs"

const int XRENGINE_MAX_CASCADES = 8;
const int XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS = 2;

struct BaseLight
{
    vec3 Color;
    float DiffuseIntensity;
    float AmbientIntensity;
    mat4 WorldToLightSpaceProjMatrix;
};

// std430 mirror of the 224-byte DirectionalShadowGpuRecord publication.
// Matrix bytes are uploaded without transpose: System.Numerics' raw M11..M44
// layout intentionally has the same transform convention as existing GLSL
// mat4 uploads in this renderer.
struct DirectionalShadowGpuRecord
{
    mat4 CurrentWorldToLight;
    mat4 RenderedWorldToLight;
    vec4 CurrentSplitBlendBias;
    vec4 RenderedSplitBlendBias;
    vec4 ReceiverOffsetsAge;
    ivec4 AtlasPacked0;
    vec4 AtlasUvScaleBias;
    vec4 AtlasDepthParams;
};

struct DirLight
{
    BaseLight Base;
    vec3 Direction;
    mat4 WorldToLightInvViewMatrix;
    mat4 WorldToLightProjMatrix;
    mat4 WorldToLightSpaceMatrix;
    int CascadeCount;
};

struct PointLight
{
    BaseLight Base;
    vec3 Position;
    float Radius;
    float Brightness;
};

struct SpotLight
{
    PointLight Base;
    vec3 Direction;
    float InnerCutoff;
    float OuterCutoff;
    float Exponent;
};
