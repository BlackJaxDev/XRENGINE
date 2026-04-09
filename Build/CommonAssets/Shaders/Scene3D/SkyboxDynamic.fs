#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 1) in vec3 FragWorldDir;

uniform float SkyboxIntensity = 1.0;
uniform float SkyTimeOfDay = 0.25;
uniform float SkyCloudCoverage = 0.45;
uniform float SkyCloudScale = 1.4;
uniform float SkyCloudSpeed = 0.02;
uniform float SkyCloudSharpness = 1.75;
uniform float SkyStarIntensity = 1.0;
uniform float SkyHorizonHaze = 1.0;
uniform float SkySunDiscSize = 0.9994;
uniform float SkyMoonDiscSize = 0.99965;

const float PI = 3.14159265359;

vec2 SafeNormalize2(vec2 v)
{
    float lenSq = dot(v, v);
    return lenSq > 1e-8 ? v * inversesqrt(lenSq) : vec2(0.0, 1.0);
}

vec3 SafeNormalize3(vec3 v)
{
    float lenSq = dot(v, v);
    return lenSq > 1e-8 ? v * inversesqrt(lenSq) : vec3(0.0, 1.0, 0.0);
}

float Hash(vec2 p)
{
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float Noise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(Hash(i + vec2(0.0, 0.0)), Hash(i + vec2(1.0, 0.0)), u.x),
               mix(Hash(i + vec2(0.0, 1.0)), Hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float Fbm(vec2 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; ++i)
    {
        v += a * Noise(p);
        p = p * 2.03 + vec2(17.0, 11.0);
        a *= 0.5;
    }
    return v;
}

vec3 NightSky(vec3 dir, float nightFactor)
{
    float horizonFade = smoothstep(-0.1, 0.35, dir.y);
    vec3 nightGradient = mix(vec3(0.01, 0.015, 0.03), vec3(0.005, 0.007, 0.015), horizonFade);
    vec2 st = SafeNormalize2(dir.xz) * 256.0 + vec2(dir.y * 73.0, dir.x * 41.0);
    float stars = step(0.9975, Hash(floor(st))) * nightFactor;
    return nightGradient + stars * vec3(1.0, 0.96, 0.9) * SkyStarIntensity;
}

void main()
{
    vec3 dir = SafeNormalize3(FragWorldDir);

    float angle = SkyTimeOfDay * PI * 2.0;
    vec3 sunDir = normalize(vec3(cos(angle), sin(angle), 0.2));
    vec3 moonDir = -sunDir;
    float sunHeight = sunDir.y;

    float dayFactor = smoothstep(-0.18, 0.1, sunHeight);
    float nightFactor = 1.0 - dayFactor;
    float duskFactor = 1.0 - abs(sunHeight) / 0.22;
    duskFactor = clamp(duskFactor, 0.0, 1.0);

    float h = clamp(dir.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 dayHorizon = vec3(0.73, 0.84, 1.0);
    vec3 dayZenith = vec3(0.15, 0.42, 0.95);
    vec3 duskHorizon = vec3(1.0, 0.45, 0.18);
    vec3 duskZenith = vec3(0.26, 0.08, 0.32);
    vec3 skyDay = mix(dayHorizon, dayZenith, pow(h, 0.45));
    vec3 skyDusk = mix(duskHorizon, duskZenith, pow(h, 0.65));
    vec3 skyBase = mix(skyDusk, skyDay, dayFactor);

    float mie = pow(max(dot(dir, sunDir), 0.0), 18.0);
    float rayleigh = pow(max(dot(dir, sunDir), 0.0), 3.0);
    vec3 sunsetScatter = (vec3(1.0, 0.34, 0.16) * mie + vec3(0.45, 0.56, 1.0) * rayleigh * 0.4) * duskFactor;
    vec3 color = skyBase + sunsetScatter;

    float horizon = 1.0 - clamp(abs(dir.y), 0.0, 1.0);
    color += vec3(0.22, 0.19, 0.15) * pow(horizon, 2.2) * SkyHorizonHaze * duskFactor;

    vec2 cloudUv = SafeNormalize2(max(abs(dir.y), 0.06) * dir.xz) * SkyCloudScale;
    cloudUv += vec2(SkyCloudSpeed * SkyTimeOfDay * 240.0, SkyCloudSpeed * SkyTimeOfDay * 120.0);
    float cloud = Fbm(cloudUv);
    cloud = smoothstep(1.0 - SkyCloudCoverage, 1.0, pow(cloud, SkyCloudSharpness));
    vec3 cloudDay = vec3(1.0, 0.98, 0.95);
    vec3 cloudNight = vec3(0.26, 0.28, 0.35);
    vec3 cloudTint = mix(cloudNight, cloudDay, dayFactor);
    color = mix(color, cloudTint + sunsetScatter * 0.35, cloud * (0.25 + 0.55 * dayFactor));

    float sunDisc = smoothstep(SkySunDiscSize, 1.0, dot(dir, sunDir));
    float sunHalo = pow(max(dot(dir, sunDir), 0.0), 64.0);
    color += vec3(1.0, 0.92, 0.78) * (sunDisc * 12.0 + sunHalo * 0.75) * dayFactor;

    float moonDisc = smoothstep(SkyMoonDiscSize, 1.0, dot(dir, moonDir));
    float moonGlow = pow(max(dot(dir, moonDir), 0.0), 48.0);
    color += vec3(0.74, 0.79, 0.94) * (moonDisc * 2.8 + moonGlow * 0.35) * nightFactor;

    float skyBlend = clamp(dayFactor + duskFactor * 0.35, 0.0, 1.0);
    vec3 fallbackColor = mix(NightSky(dir, nightFactor), skyBase, skyBlend);
    color = mix(NightSky(dir, nightFactor), color, skyBlend);
    if (any(isnan(color)) || any(isinf(color)))
        color = fallbackColor;

    OutColor = max(color, vec3(0.0)) * SkyboxIntensity;
}
