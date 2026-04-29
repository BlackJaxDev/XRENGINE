// LightStructs snippet
// Usage: #pragma snippet "LightStructs"

const int XRENGINE_MAX_CASCADES = 8;

struct BaseLight
{
    vec3 Color;
    float DiffuseIntensity;
    float AmbientIntensity;
    mat4 WorldToLightSpaceProjMatrix;
};

struct DirLight
{
    BaseLight Base;
    vec3 Direction;
    mat4 WorldToLightInvViewMatrix;
    mat4 WorldToLightProjMatrix;
    mat4 WorldToLightSpaceMatrix;
    float CascadeSplits[XRENGINE_MAX_CASCADES];
    float CascadeBlendWidths[XRENGINE_MAX_CASCADES];
    float CascadeBiasMin[XRENGINE_MAX_CASCADES];
    float CascadeBiasMax[XRENGINE_MAX_CASCADES];
    float CascadeReceiverOffsets[XRENGINE_MAX_CASCADES];
    mat4 CascadeMatrices[XRENGINE_MAX_CASCADES];
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
