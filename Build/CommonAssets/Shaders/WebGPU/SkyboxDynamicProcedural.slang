// Authored Slang counterpart of Scene3D/SkyboxDynamic.fs. Preserve its full
// atmosphere, celestial discs, moon phases, directional clouds and motion twinkle.
#include "SkyboxCommon.slang"

static const float PI = 3.14159265359;
static const float TAU = 6.28318530718;

// --- Safe normalization & pole-safe octahedral mapping (contract required) ---
float2 SafeNormalize2(float2 v)
{
    float lenSq = dot(v, v);
    return lenSq > 1e-8 ? v * rsqrt(lenSq) : float2(0.0, 1.0);
}

float3 SafeNormalize3(float3 v)
{
    float lenSq = dot(v, v);
    return lenSq > 1e-8 ? v * rsqrt(lenSq) : float3(0.0, 1.0, 0.0);
}

float2 DirectionToOctahedralPlane(float3 dir)
{
    float3 n = SafeNormalize3(dir);
    float invL1 = 1.0 / max(abs(n.x) + abs(n.y) + abs(n.z), 1e-6);
    float2 oct = n.xz * invL1;

    if (n.y < 0.0)
    {
        float2 octSign = float2(oct.x >= 0.0 ? 1.0 : -1.0, oct.y >= 0.0 ? 1.0 : -1.0);
        oct = (1.0 - abs(oct.yx)) * octSign;
    }

    return oct;
}

// --- Hashing / value noise / fbm ---
float Hash(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453123);
}

float Hash3(float3 p)
{
    p = frac(p * float3(443.8975, 397.2973, 491.1871));
    p += dot(p, p.yzx + 19.19);
    return frac((p.x + p.y) * p.z);
}

float MotionStarTwinkle(float3 cell, float seed, float amount, float speed)
{
    float motion = clamp(sky.cameraTwinkle, 0.0, 1.0);
    if (motion <= 0.001)
        return 1.0;

    float phase = sky.twinklePhase * speed + seed * 13.37;
    float phaseBase = floor(phase);
    float phaseBlend = smoothstep(0.0, 1.0, frac(phase));
    float3 phaseSeed = float3(seed * 31.0 + 7.0, seed * 17.0 + 3.0, phaseBase);
    float a = Hash3(cell + phaseSeed);
    float b = Hash3(cell + phaseSeed + float3(0.0, 0.0, 1.0));
    return 1.0 + (lerp(a, b, phaseBlend) - 0.5) * amount * motion;
}

float Noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(Hash(i + float2(0.0, 0.0)), Hash(i + float2(1.0, 0.0)), u.x),
               lerp(Hash(i + float2(0.0, 1.0)), Hash(i + float2(1.0, 1.0)), u.x), u.y);
}

// 3D value noise on a direction - seamless on the sphere, no pole distortion.
float Noise3(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * (3.0 - 2.0 * f);
    float n000 = Hash3(i);
    float n100 = Hash3(i + float3(1.0, 0.0, 0.0));
    float n010 = Hash3(i + float3(0.0, 1.0, 0.0));
    float n110 = Hash3(i + float3(1.0, 1.0, 0.0));
    float n001 = Hash3(i + float3(0.0, 0.0, 1.0));
    float n101 = Hash3(i + float3(1.0, 0.0, 1.0));
    float n011 = Hash3(i + float3(0.0, 1.0, 1.0));
    float n111 = Hash3(i + float3(1.0, 1.0, 1.0));
    return lerp(lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y),
               lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y), u.z);
}

float Fbm(float2 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; ++i)
    {
        v += a * Noise(p);
        p = p * 2.03 + float2(17.0, 11.0);
        a *= 0.5;
    }
    return v;
}

float Fbm6(float2 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 6; ++i)
    {
        v += a * Noise(p);
        p = p * 2.17 + float2(4.3, 9.1);
        a *= 0.55;
    }
    return v;
}

float Fbm3(float3 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; ++i)
    {
        v += a * Noise3(p);
        p = p * 2.03 + float3(17.0, 11.0, 5.3);
        a *= 0.5;
    }
    return v;
}

float Fbm3_6(float3 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 6; ++i)
    {
        v += a * Noise3(p);
        p = p * 2.17 + float3(4.3, 9.1, 2.7);
        a *= 0.55;
    }
    return v;
}

// --- Analytic single-scattering atmosphere (Rayleigh + Mie, ground observer) ---
float3 Atmosphere(float3 viewDir, float3 sunDir)
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
    float3 rayleighCoeff = float3(0.58, 1.35, 3.31);
    float3 mieCoeff = float3(2.1);

    // Long sun path scatters out blue -> warm horizon at sunset/sunrise
    float3 sunExt = exp(-rayleighCoeff * sunPath * 0.22 - mieCoeff * sunPath * 0.08);
    float3 viewExt = exp(-rayleighCoeff * viewPath * 0.10 - mieCoeff * viewPath * 0.06);

    float sunStrength = smoothstep(-0.14, 0.25, sunDir.y);

    float3 rayleighIn = rayleighCoeff * rayleighPhase * sunExt * (1.0 - viewExt);
    float3 mieIn = mieCoeff * miePhase * sunExt * (1.0 - viewExt) * 0.35;

    return (rayleighIn + mieIn) * sunStrength;
}

// --- Moon disc: phase shading, maria, craters, corona ---
float3 ShadeMoon(float3 dir, float3 moonDir, float3 sunDir, float nightFactor)
{
    float3 up = abs(moonDir.y) < 0.95 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 tangent = SafeNormalize3(cross(up, moonDir));
    float3 bitangent = cross(moonDir, tangent);

    float d = dot(dir, moonDir);
    if (d <= sky.moonDiscSize)
    {
        // Off-disc: corona glow only
        float corona = pow(max(d, 0.0), 96.0) * 0.35 + pow(max(d, 0.0), 24.0) * 0.05;
        return float3(0.74, 0.79, 0.94) * corona * nightFactor;
    }

    float t = dot(dir, tangent) / max(d, 1e-3);
    float b = dot(dir, bitangent) / max(d, 1e-3);
    float discRadius = sqrt(max(1.0 - sky.moonDiscSize * sky.moonDiscSize, 1e-6)) * 1.15;
    float2 local = float2(t, b);
    float rn = clamp(dot(local, local) / (discRadius * discRadius), 0.0, 1.0);

    // Pseudo-spherical normal at this disc point
    float z = sqrt(max(1.0 - rn, 0.0));
    float3 surfaceN = SafeNormalize3(tangent * (local.x / discRadius)
                                 + bitangent * (local.y / discRadius)
                                 + moonDir * z);

    // True lunar phase: the sun lights the moon from outside the view
    float phase = clamp(dot(surfaceN, sunDir), 0.0, 1.0);

    // Surface detail: maria (dark basalt plains) + crater variation
    float2 surfUv = local / discRadius;
    float maria = smoothstep(0.40, 0.58, Fbm(surfUv * 2.1 + float2(7.3, 1.9)));
    float craters = lerp(0.72, 1.0, Fbm6(surfUv * 5.5) * 0.5 + 0.5);

    float3 moonAlbedo = lerp(float3(0.82, 0.83, 0.86), float3(0.45, 0.49, 0.58), maria);
    float3 moonLit = moonAlbedo * craters * (0.06 + phase * 1.15);

    // Soft disc edge
    float edgeSoft = smoothstep(1.0, 0.85, rn);
    float3 moonColor = moonLit * edgeSoft;

    // Corona
    float coronaFalloff = max(d, 0.0);
    float corona = pow(coronaFalloff, 96.0) * 0.40 + pow(coronaFalloff, 24.0) * 0.06;
    moonColor += float3(0.74, 0.79, 0.94) * corona;

    return moonColor * nightFactor;
}

[shader("fragment")]
float4 skyFragment(SkyVertexOutput input) : SV_Target0
{
    float3 dir = normalize(input.worldDirection);

    // --- Celestial orbits ---
    float angle = sky.timeOfDay * TAU;
    float3 sunDir = SafeNormalize3(float3(cos(angle), sin(angle), 0.18));
    // Moon on a slightly tilted, slowly shifting orbit -> real phases emerge across the cycle
    float moonAngle = angle + PI + 0.25 * sin(sky.timeOfDay * TAU * 0.3);
    float3 moonDir = SafeNormalize3(float3(cos(moonAngle), sin(moonAngle), -0.22));

    // --- Day / night / dusk weighting ---
    float dayFactor = smoothstep(-0.18, 0.12, sunDir.y);
    float nightFactor = 1.0 - dayFactor;
    // Bell curve peaked at horizon-grazing sun
    float duskFactor = clamp(exp(-sunDir.y * sunDir.y * 38.0), 0.0, 1.0);

    // --- Atmospheric inscatter ---
    float3 skyBase = Atmosphere(dir, sunDir);

    // Sunset/sunrise warm horizon arch
    float horizonT = 1.0 - clamp(abs(dir.y), 0.0, 1.0);
    float3 horizonGlow = float3(1.15, 0.55, 0.22) * pow(horizonT, 3.0) * duskFactor * sky.horizonHaze;

    // Belt of Venus (anti-twilight arch on the side opposite the sun)
    float2 flatSun = SafeNormalize2(float2(sunDir.x, sunDir.z));
    float2 flatDir = SafeNormalize2(float2(dir.x, dir.z));
    float antiSun = max(-dot(flatDir, flatSun), 0.0);
    float3 beltOfVenus = float3(0.78, 0.55, 0.68) * pow(horizonT, 2.2) * antiSun * duskFactor * 0.45;

    float3 color = skyBase + horizonGlow + beltOfVenus;

    // Dusk saturation warmth
    color *= lerp(float3(1.0), float3(1.20, 0.88, 0.78), duskFactor * 0.55);

    float3 starField = float3(0.0);
    if (nightFactor > 0.001 && sky.starIntensity > 0.001)
    {
        float starDensity = 256.0;
        float3 starP = dir * starDensity;
        float3 starCell = floor(starP);
        float starHash = Hash3(starCell);
        float twinkle = MotionStarTwinkle(starCell, starHash, 0.16, 1.0);
        float smallStar = step(0.9975, starHash) * (0.55 + 0.45 * frac(starHash * 17.3)) * twinkle;

        float bigDensity = 64.0;
        float3 bigP = dir * bigDensity;
        float3 bigCell = floor(bigP);
        float bigHash = Hash3(bigCell);
        float3 bigOffset = frac(bigP) - 0.5;
        float bigStar = step(0.997, bigHash) * exp(-dot(bigOffset, bigOffset) * 60.0);
        bigStar *= MotionStarTwinkle(bigCell, bigHash, 0.10, 0.73);
        float hueSeed = Hash3(starCell + float3(11.0, 23.0, 7.0));
        float3 starColor = lerp(float3(0.85, 0.90, 1.10), float3(1.10, 0.95, 0.78), hueSeed);

        float3 stars = (smallStar + bigStar * 2.4) * starColor;

        float3 galacticUp = SafeNormalize3(float3(0.35, 0.22, 0.91));
        float bandCoord = dot(dir, galacticUp);
        float band = exp(-bandCoord * bandCoord * 38.0);
        float3 galaxyP = dir * 5.2 + galacticUp * 3.1 + float3(3.1, 7.4, 1.9);
        float mwDetail = smoothstep(0.35, 0.95, Fbm3(galaxyP));
        float3 milkyWay = lerp(float3(0.06, 0.08, 0.15), float3(0.22, 0.18, 0.28), mwDetail) * band * mwDetail * 0.35;

        float starHorizonFade = smoothstep(-0.05, 0.22, dir.y);
        starField = (stars + milkyWay) * sky.starIntensity * starHorizonFade;
    }

    // Night deep-sky base
    float3 nightBase = float3(0.006, 0.009, 0.022) * (0.4 + 0.6 * smoothstep(-0.12, 0.4, dir.y));

    // Blend day-lit atmosphere over night depending on sun elevation
    float3 night = nightBase + starField * nightFactor;
    color = lerp(night, color + night * 0.15, clamp(dayFactor + duskFactor * 0.35, 0.0, 1.0));

    // --- Seamless directional clouds ---
    float timeAdvect = sky.cloudSpeed * sky.timeOfDay * 240.0;
    float flowAngle = sky.timeOfDay * 0.35;
    float3 windDir = SafeNormalize3(float3(cos(flowAngle), 0.12, sin(flowAngle)));
    float3 cloudP = dir * sky.cloudScale * 3.0;
    float3 warped = cloudP + windDir * timeAdvect * 0.25;
    float cloudBase = Fbm3_6(warped);
    float cloudDetail = Noise3(warped * 6.5 - windDir * timeAdvect * 0.35);
    float cloudShape = cloudBase * 0.82 + cloudDetail * 0.18;
    float cloudMask = smoothstep(-0.08, 0.12, dir.y);
    float cloud = smoothstep(1.0 - sky.cloudCoverage, 1.0, pow(cloudShape, sky.cloudSharpness)) * cloudMask;

    // Cloud shading: Beer-Lambert powder effect + forward-scatter silver lining
    float muSun = dot(dir, sunDir);
    float silver = pow(max(muSun, 0.0), 8.0);
    float powder = 1.0 - exp(-cloud * 2.5);
    float3 cloudLit = lerp(float3(0.45, 0.48, 0.55), float3(1.00, 0.98, 0.93), dayFactor);
    float3 cloudShadow = lerp(float3(0.04, 0.05, 0.09), float3(0.58, 0.63, 0.75), dayFactor);
    float3 cloudColor = lerp(cloudShadow, cloudLit, powder);
    cloudColor += float3(1.30, 0.55, 0.22) * silver * duskFactor * 1.2;
    // Distant cloud atmospheric blend near the horizon
    cloudColor = lerp(cloudColor, color, smoothstep(0.5, 0.03, dir.y) * 0.55);

    color = lerp(color, cloudColor, cloud);

    // --- Sun disc with limb darkening + aureole, cloud occluded ---
    float cosSun = dot(dir, sunDir);
    float sunDisc = smoothstep(sky.sunDiscSize, 1.0, cosSun);
    float limbT = clamp((cosSun - sky.sunDiscSize) / max(1.0 - sky.sunDiscSize, 1e-4), 0.0, 1.0);
    float limbDark = lerp(0.55, 1.0, pow(limbT, 0.5));
    float3 sunColor = lerp(float3(1.4, 0.55, 0.18), float3(1.0, 0.96, 0.88), smoothstep(-0.05, 0.35, sunDir.y));
    float sunAureole = pow(max(cosSun, 0.0), 96.0) * 0.80 + pow(max(cosSun, 0.0), 32.0) * 0.12;
    float sunOcclusion = 1.0 - cloud * 0.85;
    color += sunColor * (sunDisc * 18.0 * limbDark + sunAureole) * dayFactor * sunOcclusion;

    // --- Moon ---
    float3 moonContribution = ShadeMoon(dir, moonDir, sunDir, nightFactor);
    color += moonContribution * (1.0 - cloud * 0.9);

    // NaN/Inf guard -> safe horizon gradient fallback
    if (any(isnan(color)) || any(isinf(color)))
    {
        float h = clamp(dir.y * 0.5 + 0.5, 0.0, 1.0);
        color = lerp(float3(0.05, 0.08, 0.15), float3(0.30, 0.50, 0.85), h);
    }

    return float4(max(color, float3(0.0)) * sky.intensity, 1.0);
}
