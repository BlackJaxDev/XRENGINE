#version 450

#pragma snippet "DDGIEnvironmentCapture"

layout (location = 0) out vec4 OutColor;
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
uniform float SkyCameraTwinkle = 0.0;
uniform float SkyTwinklePhase = 0.0;

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

// Integer-lattice PCG3D hash so adjacent noise cells evaluate identical bit-exact
// corner values regardless of floating-point contraction or driver optimization.
float Hash3Lattice(ivec3 cell)
{
    uvec3 v = uvec3(cell) * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    return float(v.x ^ v.y ^ v.z) * (1.0 / 4294967296.0);
}

float Hash3(vec3 p)
{
    p = fract(p * vec3(443.8975, 397.2973, 491.1871));
    p += dot(p, p.yzx + 19.19);
    return fract((p.x + p.y) * p.z);
}

float MotionStarTwinkle(vec3 cell, float seed, float amount, float speed)
{
    float motion = clamp(SkyCameraTwinkle, 0.0, 1.0);
    if (motion <= 0.001)
        return 1.0;

    float phase = SkyTwinklePhase * speed + seed * 13.37;
    float phaseBase = floor(phase);
    float phaseBlend = smoothstep(0.0, 1.0, fract(phase));
    vec3 phaseSeed = vec3(seed * 31.0 + 7.0, seed * 17.0 + 3.0, phaseBase);
    float a = Hash3(cell + phaseSeed);
    float b = Hash3(cell + phaseSeed + vec3(0.0, 0.0, 1.0));
    return 1.0 + (mix(a, b, phaseBlend) - 0.5) * amount * motion;
}

float Noise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    return mix(mix(Hash(i + vec2(0.0, 0.0)), Hash(i + vec2(1.0, 0.0)), u.x),
               mix(Hash(i + vec2(0.0, 1.0)), Hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

// 3D value noise with an integer lattice hash and C2 quintic interpolation so
// cell boundaries stay seamless and free of axis-aligned creases.
float Noise3(vec3 p)
{
    vec3 fl = floor(p);
    ivec3 i = ivec3(fl);
    vec3 f = p - fl;
    vec3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float n000 = Hash3Lattice(i);
    float n100 = Hash3Lattice(i + ivec3(1, 0, 0));
    float n010 = Hash3Lattice(i + ivec3(0, 1, 0));
    float n110 = Hash3Lattice(i + ivec3(1, 1, 0));
    float n001 = Hash3Lattice(i + ivec3(0, 0, 1));
    float n101 = Hash3Lattice(i + ivec3(1, 0, 1));
    float n011 = Hash3Lattice(i + ivec3(0, 1, 1));
    float n111 = Hash3Lattice(i + ivec3(1, 1, 1));
    return mix(mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y),
               mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y), u.z);
}

// Orthonormal 3D rotation applied between FBM octaves to break up axis-aligned lattice bias.
const mat3 FBM3_OCTAVE_ROT = mat3(
     0.00,  0.80,  0.60,
    -0.80,  0.36, -0.48,
    -0.60, -0.48,  0.64);

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

float Fbm3_2(vec3 p)
{
    float v = 0.5 * Noise3(p);
    p = (FBM3_OCTAVE_ROT * p) * 2.03 + vec3(17.0, 11.0, 5.3);
    v += 0.25 * Noise3(p);
    return v * (4.0 / 3.0);
}

float Fbm3_3(vec3 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 3; ++i)
    {
        v += a * Noise3(p);
        p = (FBM3_OCTAVE_ROT * p) * 2.03 + vec3(17.0, 11.0, 5.3);
        a *= 0.5;
    }
    return v * (8.0 / 7.0);
}

float Fbm3_4(vec3 p)
{
    float v = 0.0;
    float a = 0.5;
    float norm = 0.0;
    for (int i = 0; i < 4; ++i)
    {
        v += a * Noise3(p);
        norm += a;
        p = (FBM3_OCTAVE_ROT * p) * 2.08 + vec3(4.3, 9.1, 2.7);
        a *= 0.52;
    }
    return v / max(norm, 1e-5);
}

float Fbm3(vec3 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; ++i)
    {
        v += a * Noise3(p);
        p = (FBM3_OCTAVE_ROT * p) * 2.03 + vec3(17.0, 11.0, 5.3);
        a *= 0.5;
    }
    return v;
}

float Fbm3_6(vec3 p)
{
    float v = 0.0;
    float a = 0.5;
    float norm = 0.0;
    for (int i = 0; i < 6; ++i)
    {
        v += a * Noise3(p);
        norm += a;
        p = (FBM3_OCTAVE_ROT * p) * 2.08 + vec3(4.3, 9.1, 2.7);
        a *= 0.52;
    }
    return v / max(norm, 1e-5);
}

// Intersects the sky ray with a curved planetary cloud layer above the observer so clouds
// foreshorten naturally toward the horizon without a zenith pole pinch or horizon singularity.
float ProjectCloudDomeDistance(vec3 dir)
{
    const float earthRadius = 16.0;
    const float cloudAltitude = 1.0;
    float dy = max(dir.y, 0.0);
    return -earthRadius * dy + sqrt(earthRadius * earthRadius * dy * dy + 2.0 * earthRadius * cloudAltitude + cloudAltitude * cloudAltitude);
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
    vec3 dir = XRENGINE_DDGIEnvironmentDirection(FragWorldDir);

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

    vec3 starField = vec3(0.0);
    if (nightFactor > 0.001 && SkyStarIntensity > 0.001)
    {
        float starDensity = 256.0;
        vec3 starP = dir * starDensity;
        vec3 starCell = floor(starP);
        ivec3 starCellInt = ivec3(starCell);
        float starHash = Hash3(starCell);
        vec3 starCenter = vec3(
            0.22 + 0.56 * Hash3Lattice(starCellInt + ivec3(19, 37, 53)),
            0.22 + 0.56 * Hash3Lattice(starCellInt + ivec3(71, 13, 89)),
            0.22 + 0.56 * Hash3Lattice(starCellInt + ivec3(41, 97, 29)));
        vec3 starDelta = (starP - starCell) - starCenter;
        starDelta -= dir * dot(starDelta, dir);
        float smallStarDisc = exp(-dot(starDelta, starDelta) * 58.0);
        float twinkle = MotionStarTwinkle(starCell, starHash, 0.16, 1.0);
        float smallStar = step(0.9965, starHash) * smallStarDisc * (0.65 + 0.55 * fract(starHash * 17.3)) * twinkle;

        float bigDensity = 64.0;
        vec3 bigP = dir * bigDensity;
        vec3 bigCell = floor(bigP);
        ivec3 bigCellInt = ivec3(bigCell);
        float bigHash = Hash3(bigCell);
        vec3 bigCenter = vec3(
            0.25 + 0.50 * Hash3Lattice(bigCellInt + ivec3(17, 59, 83)),
            0.25 + 0.50 * Hash3Lattice(bigCellInt + ivec3(43, 23, 67)),
            0.25 + 0.50 * Hash3Lattice(bigCellInt + ivec3(91, 11, 31)));
        vec3 bigOffset = (bigP - bigCell) - bigCenter;
        bigOffset -= dir * dot(bigOffset, dir);
        float bigStar = step(0.997, bigHash) * exp(-dot(bigOffset, bigOffset) * 72.0);
        bigStar *= MotionStarTwinkle(bigCell, bigHash, 0.10, 0.73);
        float hueSeed = Hash3(starCell + vec3(11.0, 23.0, 7.0));
        vec3 starColor = mix(vec3(0.85, 0.90, 1.10), vec3(1.10, 0.95, 0.78), hueSeed);

        vec3 stars = (smallStar + bigStar * 2.4) * starColor;

        vec3 galacticUp = SafeNormalize3(vec3(0.35, 0.22, 0.91));
        float bandCoord = dot(dir, galacticUp);
        float band = exp(-bandCoord * bandCoord * 38.0);
        vec3 galaxyP = dir * 5.2 + galacticUp * 3.1 + vec3(3.1, 7.4, 1.9);
        float mwDetail = smoothstep(0.35, 0.95, Fbm3(galaxyP));
        vec3 milkyWay = mix(vec3(0.06, 0.08, 0.15), vec3(0.22, 0.18, 0.28), mwDetail) * band * mwDetail * 0.35;

        float starHorizonFade = smoothstep(-0.05, 0.22, dir.y);
        starField = (stars + milkyWay) * SkyStarIntensity * starHorizonFade;
    }

    // Night deep-sky base
    vec3 nightBase = vec3(0.006, 0.009, 0.022) * (0.4 + 0.6 * smoothstep(-0.12, 0.4, dir.y));

    // Blend day-lit atmosphere over night depending on sun elevation
    vec3 night = nightBase + starField * nightFactor;
    color = mix(night, color + night * 0.15, clamp(dayFactor + duskFactor * 0.35, 0.0, 1.0));

    // --- Seamless directional clouds projected onto a curved planetary cloud dome ---
    // Skip below-horizon and zero-coverage pixels before evaluating 3D cloud FBM octaves.
    float cloud = 0.0;
    if (dir.y > 0.0 && SkyCloudCoverage > 0.001)
    {
        float timeAdvect = SkyCloudSpeed * SkyTimeOfDay * 240.0;
        vec3 windDir = SafeNormalize3(vec3(0.86, 0.0, 0.51));
        float cloudDomeDist = ProjectCloudDomeDistance(dir);
        vec3 cloudP = dir * SkyCloudScale * (cloudDomeDist * 0.72);
        cloudP.y *= 0.25;
        vec3 advected = cloudP + windDir * (timeAdvect * 0.45);
        vec3 cloudWarp = vec3(
            Fbm3_2(advected * 0.55 + vec3(3.1, 1.7, 7.4)) - 0.5,
            0.0,
            Fbm3_2(advected * 0.55 + vec3(11.7, 2.9, 2.3)) - 0.5);
        vec3 warped = advected + cloudWarp * 0.55;
        float cloudBase = Fbm3_4(warped);
        float cloudDetail = Noise3(warped * 4.5 - windDir * (timeAdvect * 0.25));
        float cloudShape = smoothstep(0.26, 0.78, cloudBase * 0.80 + cloudDetail * 0.20);
        float cloudMask = smoothstep(-0.08, 0.12, dir.y);
        float horizonCloudFade = smoothstep(0.0, 0.10, dir.y);
        cloud = smoothstep(1.0 - SkyCloudCoverage, 1.0, pow(cloudShape, SkyCloudSharpness)) * cloudMask * horizonCloudFade;

        if (cloud > 0.001)
        {
            // Cloud shading: directional sun self-shadowing + Beer-Lambert powder + silver lining
            float sunOffsetDensity = Fbm3_3(warped + vec3(sunDir.x, 0.0, sunDir.z) * 0.22);
            float sunLitTerm = clamp((cloudBase - sunOffsetDensity) * 2.4 + 0.55, 0.0, 1.0);
            float muSun = dot(dir, sunDir);
            float silver = pow(max(muSun, 0.0), 8.0);
            float powder = 1.0 - exp(-cloud * 2.5);
            float litWeight = clamp(mix(powder, sunLitTerm, 0.55), 0.0, 1.0);
            vec3 cloudLit = mix(vec3(0.11, 0.14, 0.22), vec3(1.00, 0.98, 0.93), dayFactor);
            vec3 cloudShadow = mix(vec3(0.02, 0.03, 0.06), vec3(0.55, 0.61, 0.74), dayFactor);
            vec3 cloudColor = mix(cloudShadow, cloudLit, litWeight);
            cloudColor += vec3(1.30, 0.55, 0.22) * silver * duskFactor * 1.2;
            // Distant cloud atmospheric perspective blend near the horizon
            cloudColor = mix(cloudColor, color, smoothstep(0.42, 0.02, dir.y) * 0.72);
            color = mix(color, cloudColor, cloud);
        }
    }

    // --- Sun disc with limb darkening + aureole, cloud occluded ---
    if (dayFactor > 0.001)
    {
        float cosSun = dot(dir, sunDir);
        float sunDisc = smoothstep(SkySunDiscSize, 1.0, cosSun);
        float limbT = clamp((cosSun - SkySunDiscSize) / max(1.0 - SkySunDiscSize, 1e-4), 0.0, 1.0);
        float limbDark = mix(0.55, 1.0, pow(limbT, 0.5));
        vec3 sunColor = mix(vec3(1.4, 0.55, 0.18), vec3(1.0, 0.96, 0.88), smoothstep(-0.05, 0.35, sunDir.y));
        float sunAureole = pow(max(cosSun, 0.0), 96.0) * 0.80 + pow(max(cosSun, 0.0), 32.0) * 0.12;
        float sunOcclusion = 1.0 - cloud * 0.85;
        color += sunColor * (sunDisc * 18.0 * limbDark + sunAureole) * dayFactor * sunOcclusion;
    }

    // --- Moon ---
    if (nightFactor > 0.001)
    {
        vec3 moonContribution = ShadeMoon(dir, moonDir, sunDir, nightFactor);
        color += moonContribution * (1.0 - cloud * 0.9);
    }

    // NaN/Inf guard -> safe horizon gradient fallback
    if (any(isnan(color)) || any(isinf(color)))
    {
        float h = clamp(dir.y * 0.5 + 0.5, 0.0, 1.0);
        color = mix(vec3(0.05, 0.08, 0.15), vec3(0.30, 0.50, 0.85), h);
    }

    OutColor = vec4(max(color, vec3(0.0)) * SkyboxIntensity, 1.0);
}
