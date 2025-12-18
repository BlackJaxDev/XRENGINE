// Math Utilities Snippet

#ifndef PI
#define PI 3.14159265359
#endif

#ifndef TAU
#define TAU 6.28318530718
#endif

#ifndef EPSILON
#define EPSILON 0.0001
#endif

float XRENGINE_Saturate(float x)
{
    return clamp(x, 0.0, 1.0);
}

vec2 XRENGINE_Saturate2(vec2 x)
{
    return clamp(x, vec2(0.0), vec2(1.0));
}

vec3 XRENGINE_Saturate3(vec3 x)
{
    return clamp(x, vec3(0.0), vec3(1.0));
}

vec4 XRENGINE_Saturate4(vec4 x)
{
    return clamp(x, vec4(0.0), vec4(1.0));
}

float XRENGINE_Remap(float value, float inMin, float inMax, float outMin, float outMax)
{
    return outMin + (value - inMin) * (outMax - outMin) / (inMax - inMin);
}

float XRENGINE_SmoothStep01(float x)
{
    return x * x * (3.0 - 2.0 * x);
}

float XRENGINE_SmootherStep01(float x)
{
    return x * x * x * (x * (x * 6.0 - 15.0) + 10.0);
}
