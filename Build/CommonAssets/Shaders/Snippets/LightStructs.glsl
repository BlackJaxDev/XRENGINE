// LightStructs snippet
// Usage: #pragma snippet "LightStructs"

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
