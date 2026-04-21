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
const float TAU = 6.28318530718;

// --- Safe normalization & pole-safe octahedral mapping (contract required) ---
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

vec2 DirectionToOctahedralPlane(vec3 dir)
{
    vec3 n = SafeNormalize3(dir);
    float invL1 = 1.0 / max(abs(n.x) + abs(n.y) + abs(n.z), 1e-6);
    vec2 oct = n.xz * invL1;

    if (n.y < 0.0)
    {
        vec2 octSign = vec2(oct.x >= 0.0 ? 1.0 : -1.0, oct.y >= 0.0 ? 1.0 : -1.0);
        oct = (1.0 - abs(oct.yx)) * octSign;
    }

    return oct;
}

// --- Hashing / value noise / fbm ---
float Hash(vec2 p)
{
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float Hash3(vec3 p)
{
    p = fract(p * vec3(443.8975, 397.2973, 491.1871));
    p += dot(p, p.yzx + 19.19);
    return fract((p.x + p.y) * p.z);
}

float Noise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(Hash(i + vec2(0.0, 0.0)), Hash(i + vec2(1.0, 0.0)), u.x),
               mix(Hash(i + vec2(0.0, 1.0)), Hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

// 3D value noise on a direction - seamless on the sphere, no pole distortion.
float Noise3(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);
    float n000 = Hash3(i);
    float n100 = Hash3(i + vec3(1.0, 0.0, 0.0));
    float n010 = Hash3(i + vec3(0.0, 1.0, 0.0));
    float n110 = Hash3(i + vec3(1.0, 1.0, 0.0));
    float n001 = Hash3(i + vec3(0.0, 0.0, 1.0));
    float n101 = Hash3(i + vec3(1.0, 0.0, 1.0));
    float n011 = Hash3(i + vec3(0.0, 1.0, 1.0));
    float n111 = Hash3(i + vec3(1.0, 1.0, 1.0));
    return mix(mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y),
               mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y), u.z);
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

float Fbm6(vec2 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 6; ++i)
    {
        v += a * Noise(p);
        p = p * 2.17 + vec2(4.3, 9.1);
        a *= 0.55;
    }
    return v;
}

float Fbm3(vec3 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; ++i)
    {
        v += a * Noise3(p);
        p = p * 2.03 + vec3(17.0, 11.0, 5.3);
        a *= 0.5;
    }
    return v;
}

float Fbm3_6(vec3 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 6; ++i)
    {
        v += a * Noise3(p);
        p = p * 2.17 + vec3(4.3, 9.1, 2.7);
        a *= 0.55;
    }
    return v;
}

// --- Analytic single-scattering atmosphere (Rayleigh + Mie, ground observer) ---
vec3 Atmosphere(vec3 viewDir, vec3 sunDir)
{
    float cosZen = max(viewDir.y, -0.1);
    float cosSunZen = max(sunDir.y, -0.1);
    float mu = dot(viewDir, sunDir);

    // Optical-depth proxies, thicker toward the horizon
    float viewPath = 1.0 / (cosZen + 0.15);
    float sunPath = 1.0 / (cosSunZen + 0.15);

    // Phase functions
    float rayleighPhase = 0.75 * (1.0 + mu * mu);
    float g = 0.76;
    float g2 = g * g;
    float miePhase = (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * mu, 1e-4), 1.5);
    miePhase *= 0.0597;

    // Wavelength-weighted scattering (tuned for visual fidelity, not radiometric truth)
    vec3 rayleighCoeff = vec3(0.58, 1.35, 3.31);
    vec3 mieCoeff = vec3(2.1);

    // Long sun path scatters out blue -> warm horizon at sunset/sunrise
    vec3 sunExt = exp(-rayleighCoeff * sunPath * 0.22 - mieCoeff * sunPath * 0.08);
    vec3 viewExt = exp(-rayleighCoeff * viewPath * 0.10 - mieCoeff * viewPath * 0.06);

    float sunStrength = smoothstep(-0.14, 0.25, sunDir.y);

    vec3 rayleighIn = rayleighCoeff * rayleighPhase * sunExt * (1.0 - viewExt);
    vec3 mieIn = mieCoeff * miePhase * sunExt * (1.0 - viewExt) * 0.35;

    return (rayleighIn + mieIn) * sunStrength;
}

// --- Moon disc: phase shading, maria, craters, corona ---
vec3 ShadeMoon(vec3 dir, vec3 moonDir, vec3 sunDir, float nightFactor)
{
    vec3 up = abs(moonDir.y) < 0.95 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent = SafeNormalize3(cross(up, moonDir));
    vec3 bitangent = cross(moonDir, tangent);

    float d = dot(dir, moonDir);
    if (d <= SkyMoonDiscSize)
    {
        // Off-disc: corona glow only
        float corona = pow(max(d, 0.0), 96.0) * 0.35 + pow(max(d, 0.0), 24.0) * 0.05;
        return vec3(0.74, 0.79, 0.94) * corona * nightFactor;
    }

    float t = dot(dir, tangent) / max(d, 1e-3);
    float b = dot(dir, bitangent) / max(d, 1e-3);
    float discRadius = sqrt(max(1.0 - SkyMoonDiscSize * SkyMoonDiscSize, 1e-6)) * 1.15;
    vec2 local = vec2(t, b);
    float rn = clamp(dot(local, local) / (discRadius * discRadius), 0.0, 1.0);

    // Pseudo-spherical normal at this disc point
    float z = sqrt(max(1.0 - rn, 0.0));
    vec3 surfaceN = SafeNormalize3(tangent * (local.x / discRadius)
                                 + bitangent * (local.y / discRadius)
                                 + moonDir * z);

    // True lunar phase: the sun lights the moon from outside the view
    float phase = clamp(dot(surfaceN, sunDir), 0.0, 1.0);

    // Surface detail: maria (dark basalt plains) + crater variation
    vec2 surfUv = local / discRadius;
    float maria = smoothstep(0.40, 0.58, Fbm(surfUv * 2.1 + vec2(7.3, 1.9)));
    float craters = mix(0.72, 1.0, Fbm6(surfUv * 5.5) * 0.5 + 0.5);

    vec3 moonAlbedo = mix(vec3(0.82, 0.83, 0.86), vec3(0.45, 0.49, 0.58), maria);
    vec3 moonLit = moonAlbedo * craters * (0.06 + phase * 1.15);

    // Soft disc edge
    float edgeSoft = smoothstep(1.0, 0.85, rn);
    vec3 moonColor = moonLit * edgeSoft;

    // Corona
    float coronaFalloff = max(d, 0.0);
    float corona = pow(coronaFalloff, 96.0) * 0.40 + pow(coronaFalloff, 24.0) * 0.06;
    moonColor += vec3(0.74, 0.79, 0.94) * corona;

    return moonColor * nightFactor;
}

void main()
{
    vec3 dir = SafeNormalize3(FragWorldDir);

    // --- Celestial orbits ---
    float angle = SkyTimeOfDay * TAU;
    vec3 sunDir = SafeNormalize3(vec3(cos(angle), sin(angle), 0.18));
    // Moon on a slightly tilted, slowly shifting orbit -> real phases emerge across the cycle
    float moonAngle = angle + PI + 0.25 * sin(SkyTimeOfDay * TAU * 0.3);
    vec3 moonDir = SafeNormalize3(vec3(cos(moonAngle), sin(moonAngle), -0.22));

    // --- Day / night / dusk weighting ---
    float dayFactor = smoothstep(-0.18, 0.12, sunDir.y);
    float nightFactor = 1.0 - dayFactor;
    // Bell curve peaked at horizon-grazing sun
    float duskFactor = clamp(exp(-sunDir.y * sunDir.y * 38.0), 0.0, 1.0);

    // --- Atmospheric inscatter ---
    vec3 skyBase = Atmosphere(dir, sunDir);

    // Sunset/sunrise warm horizon arch
    float horizonT = 1.0 - clamp(abs(dir.y), 0.0, 1.0);
    vec3 horizonGlow = vec3(1.15, 0.55, 0.22) * pow(horizonT, 3.0) * duskFactor * SkyHorizonHaze;

    // Belt of Venus (anti-twilight arch on the side opposite the sun)
    vec2 flatSun = SafeNormalize2(vec2(sunDir.x, sunDir.z));
    vec2 flatDir = SafeNormalize2(vec2(dir.x, dir.z));
    float antiSun = max(-dot(flatDir, flatSun), 0.0);
    vec3 beltOfVenus = vec3(0.78, 0.55, 0.68) * pow(horizonT, 2.2) * antiSun * duskFactor * 0.45;

    vec3 color = skyBase + horizonGlow + beltOfVenus;

    // Dusk saturation warmth
    color *= mix(vec3(1.0), vec3(1.20, 0.88, 0.78), duskFactor * 0.55);

    // --- Stars + Milky Way (sampled directly on the 3D direction - no seams, no pole stretch) ---
    float starDensity = 256.0;
    vec3 starP = dir * starDensity;
    vec3 starCell = floor(starP);
    float starHash = Hash3(starCell);
    float twinkle = 0.65 + 0.35 * sin(TAU * (SkyTimeOfDay * 360.0 + starHash * 53.0));
    float smallStar = step(0.9975, starHash) * (0.55 + 0.45 * fract(starHash * 17.3)) * twinkle;

    // Bigger, rarer stars with a soft gaussian disc and color variation
    float bigDensity = 64.0;
    vec3 bigP = dir * bigDensity;
    vec3 bigCell = floor(bigP);
    float bigHash = Hash3(bigCell);
    vec3 bigOffset = fract(bigP) - 0.5;
    float bigStar = step(0.997, bigHash) * exp(-dot(bigOffset, bigOffset) * 60.0);
    bigStar *= 0.6 + 0.4 * sin(TAU * (SkyTimeOfDay * 180.0 + bigHash * 91.0));
    float hueSeed = Hash3(starCell + vec3(11.0, 23.0, 7.0));
    vec3 starColor = mix(vec3(0.85, 0.90, 1.10), vec3(1.10, 0.95, 0.78), hueSeed);

    vec3 stars = (smallStar + bigStar * 2.4) * starColor;

    // Milky Way band along a tilted galactic plane - detail from 3D fbm on the direction
    vec3 galacticUp = SafeNormalize3(vec3(0.35, 0.22, 0.91));
    float bandCoord = dot(dir, galacticUp);
    float band = exp(-bandCoord * bandCoord * 38.0);
    float mwDetail = smoothstep(0.35, 0.95, Fbm3_6(dir * 6.2));
    vec3 milkyWay = mix(vec3(0.06, 0.08, 0.15), vec3(0.22, 0.18, 0.28), mwDetail) * band * mwDetail * 0.35;

    float starHorizonFade = smoothstep(-0.05, 0.22, dir.y);
    vec3 starField = (stars + milkyWay) * SkyStarIntensity * starHorizonFade;

    // Night deep-sky base
    vec3 nightBase = vec3(0.006, 0.009, 0.022) * (0.4 + 0.6 * smoothstep(-0.12, 0.4, dir.y));

    // Blend day-lit atmosphere over night depending on sun elevation
    vec3 night = nightBase + starField * nightFactor;
    color = mix(night, color + night * 0.15, clamp(dayFactor + duskFactor * 0.35, 0.0, 1.0));

    // --- Volumetric-style clouds sampled in 3D on the direction (seamless) ---
    float timeAdvect = SkyCloudSpeed * SkyTimeOfDay * 240.0;
    // Flow direction perpendicular to the vertical axis, slowly rotating over time
    float flowAngle = SkyTimeOfDay * 0.35;
    vec3 flow = vec3(cos(flowAngle), 0.0, sin(flowAngle)) * timeAdvect * 0.25;
    vec3 cloudP = dir * SkyCloudScale * 3.0 + flow;

    vec3 warp = vec3(Fbm3(cloudP * 1.3 + vec3(3.2, 7.1, 0.5)),
                     Fbm3(cloudP * 1.3 + vec3(1.7, 9.3, 4.2)),
                     Fbm3(cloudP * 1.3 + vec3(6.1, 2.8, 8.4))) - 0.5;
    vec3 warped = cloudP + warp * 1.2;
    float cloudBase = Fbm3_6(warped);
    float cloudDetail = Fbm3(warped * 3.7 - flow * 0.3);
    float cloudShape = cloudBase * 0.75 + cloudDetail * 0.25;

    float cloudMask = smoothstep(-0.08, 0.12, dir.y);
    float cloud = smoothstep(1.0 - SkyCloudCoverage, 1.0, pow(cloudShape, SkyCloudSharpness)) * cloudMask;

    // Cloud shading: Beer-Lambert powder effect + forward-scatter silver lining
    float muSun = dot(dir, sunDir);
    float silver = pow(max(muSun, 0.0), 8.0);
    float powder = 1.0 - exp(-cloud * 2.5);
    vec3 cloudLit = mix(vec3(0.45, 0.48, 0.55), vec3(1.00, 0.98, 0.93), dayFactor);
    vec3 cloudShadow = mix(vec3(0.04, 0.05, 0.09), vec3(0.58, 0.63, 0.75), dayFactor);
    vec3 cloudColor = mix(cloudShadow, cloudLit, powder);
    cloudColor += vec3(1.30, 0.55, 0.22) * silver * duskFactor * 1.2;
    // Distant cloud atmospheric blend near the horizon
    cloudColor = mix(cloudColor, color, smoothstep(0.5, 0.03, dir.y) * 0.55);

    color = mix(color, cloudColor, cloud);

    // --- Sun disc with limb darkening + aureole, cloud occluded ---
    float cosSun = dot(dir, sunDir);
    float sunDisc = smoothstep(SkySunDiscSize, 1.0, cosSun);
    float limbT = clamp((cosSun - SkySunDiscSize) / max(1.0 - SkySunDiscSize, 1e-4), 0.0, 1.0);
    float limbDark = mix(0.55, 1.0, pow(limbT, 0.5));
    vec3 sunColor = mix(vec3(1.4, 0.55, 0.18), vec3(1.0, 0.96, 0.88), smoothstep(-0.05, 0.35, sunDir.y));
    float sunAureole = pow(max(cosSun, 0.0), 96.0) * 0.80 + pow(max(cosSun, 0.0), 32.0) * 0.12;
    float sunOcclusion = 1.0 - cloud * 0.85;
    color += sunColor * (sunDisc * 18.0 * limbDark + sunAureole) * dayFactor * sunOcclusion;

    // --- Moon ---
    vec3 moonContribution = ShadeMoon(dir, moonDir, sunDir, nightFactor);
    color += moonContribution * (1.0 - cloud * 0.9);

    // NaN/Inf guard -> safe horizon gradient fallback
    if (any(isnan(color)) || any(isinf(color)))
    {
        float h = clamp(dir.y * 0.5 + 0.5, 0.0, 1.0);
        color = mix(vec3(0.05, 0.08, 0.15), vec3(0.30, 0.50, 0.85), h);
    }

    OutColor = max(color, vec3(0.0)) * SkyboxIntensity;
}
