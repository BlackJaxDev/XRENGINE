#version 450

layout (location = 0) out vec4 OutColor;

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;

uniform sampler2D Texture0; // Scene color grab.
uniform sampler2D Texture1; // Scene depth grab.

uniform vec3 CameraPosition;
uniform float CameraNearZ;
uniform float CameraFarZ;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform float RenderTime;

uniform vec4 WaterShallowColor;
uniform vec4 WaterDeepColor;
uniform float WaterTransparency;
uniform float RefractionStrength;
uniform float DepthBlurRadius;
uniform float CausticIntensity;
uniform float CausticScale;
uniform float FoamIntensity;
uniform float FoamThreshold;
uniform float FoamSoftness;
uniform float EddyIntensity;
uniform float EddyRadius;

uniform int InteractorSphereCount;
uniform vec4 InteractorSphere0;
uniform vec4 InteractorSphere1;
uniform vec4 InteractorSphere2;
uniform vec4 InteractorSphere3;

uniform int InteractorCapsuleCount;
uniform vec4 InteractorCapsuleStart0;
uniform vec4 InteractorCapsuleEnd0;
uniform vec4 InteractorCapsuleStart1;
uniform vec4 InteractorCapsuleEnd1;
uniform vec4 InteractorCapsuleStart2;
uniform vec4 InteractorCapsuleEnd2;
uniform vec4 InteractorCapsuleStart3;
uniform vec4 InteractorCapsuleEnd3;

#pragma snippet "DepthUtils"

float Hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

vec4 GetSphere(int index)
{
    if (index == 0) return InteractorSphere0;
    if (index == 1) return InteractorSphere1;
    if (index == 2) return InteractorSphere2;
    return InteractorSphere3;
}

vec4 GetCapsuleStart(int index)
{
    if (index == 0) return InteractorCapsuleStart0;
    if (index == 1) return InteractorCapsuleStart1;
    if (index == 2) return InteractorCapsuleStart2;
    return InteractorCapsuleStart3;
}

vec4 GetCapsuleEnd(int index)
{
    if (index == 0) return InteractorCapsuleEnd0;
    if (index == 1) return InteractorCapsuleEnd1;
    if (index == 2) return InteractorCapsuleEnd2;
    return InteractorCapsuleEnd3;
}

float DistanceToCapsule(vec3 point, vec3 a, vec3 b)
{
    vec3 ab = b - a;
    float t = dot(point - a, ab) / max(dot(ab, ab), 1e-5);
    t = clamp(t, 0.0, 1.0);
    return length(point - (a + t * ab));
}

float ComputeInteractorEddyMask(vec3 worldPos)
{
    float mask = 0.0;
    int sphereCount = clamp(InteractorSphereCount, 0, 4);
    for (int i = 0; i < sphereCount; ++i)
    {
        vec4 s = GetSphere(i);
        float d = length(worldPos - s.xyz) - s.w;
        mask += exp(-max(d, 0.0) / max(EddyRadius, 0.001));
    }

    int capsuleCount = clamp(InteractorCapsuleCount, 0, 4);
    for (int i = 0; i < capsuleCount; ++i)
    {
        vec4 a = GetCapsuleStart(i);
        vec4 b = GetCapsuleEnd(i);
        float d = DistanceToCapsule(worldPos, a.xyz, b.xyz) - a.w;
        mask += exp(-max(d, 0.0) / max(EddyRadius, 0.001));
    }

    return clamp(mask * EddyIntensity, 0.0, 1.0);
}

float CausticPattern(vec2 p)
{
    p *= CausticScale;
    float w0 = sin(p.x + RenderTime * 1.8);
    float w1 = sin(p.y * 1.31 - RenderTime * 1.3);
    float w2 = sin((p.x + p.y) * 0.7 + RenderTime * 2.1);
    float h = Hash12(floor(p));
    return clamp((w0 + w1 + w2) * 0.166 + h * 0.45 + 0.45, 0.0, 1.0);
}

void main()
{
    vec2 screenSize = vec2(max(ScreenWidth, 1.0), max(ScreenHeight, 1.0));
    vec2 uv = gl_FragCoord.xy / screenSize;
    vec3 normal = normalize(FragNorm);
    vec3 viewDir = normalize(CameraPosition - FragPos);

    float rawSceneDepth = texture(Texture1, uv).r;
    float sceneLinear = XRENGINE_LinearizeDepth(rawSceneDepth, CameraNearZ, CameraFarZ);
    float waterLinear = XRENGINE_LinearizeDepth(gl_FragCoord.z, CameraNearZ, CameraFarZ);
    float depthDelta = clamp(sceneLinear - waterLinear, 0.0, CameraFarZ);
    float depthLerp = clamp(depthDelta / 8.0, 0.0, 1.0);

    vec2 refractOffset = normal.xz * RefractionStrength * (0.2 + depthLerp * 0.8);
    vec2 refractedUv = clamp(uv + refractOffset, vec2(0.001), vec2(0.999));

    vec4 refracted = vec4(0.0);
    float blur = DepthBlurRadius * (0.2 + depthLerp);
    vec2 texel = 1.0 / screenSize;
    const vec2 taps[5] = vec2[5](
        vec2(0.0, 0.0),
        vec2(1.0, 0.0),
        vec2(-1.0, 0.0),
        vec2(0.0, 1.0),
        vec2(0.0, -1.0));
    for (int i = 0; i < 5; ++i)
    {
        refracted += texture(Texture0, refractedUv + taps[i] * texel * blur);
    }
    refracted *= 0.2;

    float fresnel = pow(1.0 - max(dot(viewDir, normal), 0.0), 5.0);
    vec3 waterTint = mix(WaterShallowColor.rgb, WaterDeepColor.rgb, depthLerp);

    float caustics = CausticPattern(FragPos.xz + normal.xz * 0.45) * CausticIntensity * depthLerp;
    float eddyMask = ComputeInteractorEddyMask(FragPos);
    float eddyFoam = 0.5 + 0.5 * sin(RenderTime * 8.0 + dot(FragPos.xz, vec2(2.7, -3.1)));
    float foamEdge = smoothstep(FoamThreshold - FoamSoftness, FoamThreshold + FoamSoftness, 1.0 - abs(normal.y));
    float foam = clamp((foamEdge + eddyMask * eddyFoam) * FoamIntensity, 0.0, 1.0);

    vec3 absorption = refracted.rgb * mix(1.0, 0.75, depthLerp);
    vec3 litWater = mix(absorption, waterTint, 0.35 + fresnel * 0.45);
    litWater += caustics * vec3(0.13, 0.20, 0.12);
    litWater = mix(litWater, vec3(0.92, 0.96, 0.98), foam);

    float alpha = clamp(WaterTransparency + depthLerp * 0.25 + foam * 0.2, 0.03, 0.98);
    OutColor = vec4(litWater, alpha);
}
