import { moonRadianceBounds } from './sky-moon-reference.mjs';

const clamp = value => Math.max(0, Math.min(1, value));
const mix = (left, right, weight) => left + (right - left) * weight;
const dot = (left, right) => left.reduce((sum, value, index) => sum + value * right[index], 0);
const normalize = vector => vector.map(value => value / Math.hypot(...vector));
function smooth(left, right, value) {
    const t = clamp((value - left) / (right - left));
    return t * t * (3 - 2 * t);
}

/** Evaluates the authored clear atmosphere from a camera ray, without GPU input. */
export function clearSkyRadiance(ray, timeOfDay, intensity, sunDiscSize = null, moonDiscSize = null) {
    const direction = normalize(ray), time = Math.fround(timeOfDay);
    const angle = time * Math.PI * 2;
    const sun = normalize([Math.cos(angle), Math.sin(angle), 0.18]);
    const moonAngle = angle + Math.PI + 0.25 * Math.sin(time * Math.PI * 2 * 0.3);
    const moon = normalize([Math.cos(moonAngle), Math.sin(moonAngle), -0.22]);
    const sunDot = dot(direction, sun), moonDot = dot(direction, moon);
    if ((sunDiscSize === null && sunDot >= 0.9994) || (moonDiscSize === null && moonDot >= 0.99965))
        throw new Error('BrowserSmoke.SkyReferenceScope: a sample entered an unqualified celestial disc.');

    const day = smooth(-0.18, 0.12, sun[1]), nightWeight = 1 - day;
    const dusk = Math.exp(-(sun[1] ** 2) * 38);
    const horizon = 1 - Math.abs(direction[1]);
    const antiSun = Math.max(-dot(normalize([direction[0], direction[2]]), normalize([sun[0], sun[2]])), 0);
    const viewPath = 1 / (Math.max(direction[1], -0.1) + 0.15);
    const sunPath = 1 / (Math.max(sun[1], -0.1) + 0.15);
    const rayleighPhase = 0.75 * (1 + sunDot ** 2);
    const miePhase = (1 - 0.76 ** 2) * 0.0597 / Math.max(1 + 0.76 ** 2 - 1.52 * sunDot, 0.0001) ** 1.5;
    const sunAureole = Math.max(sunDot, 0) ** 96 * 0.8 + Math.max(sunDot, 0) ** 32 * 0.12;
    const threshold = sunDiscSize === null ? 0 : Math.fround(sunDiscSize);
    const sunDisc = sunDiscSize === null ? 0 : smooth(threshold, 1, sunDot);
    const limb = sunDiscSize === null ? 0 : mix(0.55, 1,
        Math.sqrt(clamp((sunDot - threshold) / Math.max(1 - threshold, Math.fround(0.0001)))));
    const moonCorona = (Math.max(moonDot, 0) ** 96 * 0.35 + Math.max(moonDot, 0) ** 24 * 0.05) * nightWeight;
    const moonColor = moonDiscSize === null ? [0.74, 0.79, 0.94].map(value => value * moonCorona)
        : moonRadianceBounds(direction, moon, sun, nightWeight, Math.fround(moonDiscSize));
    const evaluate = moonContribution => {
        const color = [0.58, 1.35, 3.31].map((rayleigh, channel) => {
            const sunExtinction = Math.exp(-rayleigh * sunPath * 0.22 - 2.1 * sunPath * 0.08);
            const viewExtinction = Math.exp(-rayleigh * viewPath * 0.1 - 2.1 * viewPath * 0.06);
            const atmosphere = (rayleigh * rayleighPhase + 2.1 * miePhase * 0.35) *
                sunExtinction * (1 - viewExtinction) * smooth(-0.14, 0.25, sun[1]);
            const glow = [1.15, 0.55, 0.22][channel] * horizon ** 3 * dusk;
            const belt = [0.78, 0.55, 0.68][channel] * horizon ** 2.2 * antiSun * dusk * 0.45;
            const warm = (atmosphere + glow + belt) * mix(1, [1.2, 0.88, 0.78][channel], dusk * 0.55);
            const night = [0.006, 0.009, 0.022][channel] * (0.4 + 0.6 * smooth(-0.12, 0.4, direction[1]));
            const sunColor = mix([1.4, 0.55, 0.18][channel], [1, 0.96, 0.88][channel], smooth(-0.05, 0.35, sun[1]));
            return Math.max(0, mix(night, warm + night * 0.15, clamp(day + dusk * 0.35)) +
                sunColor * (sunDisc * 18 * limb + sunAureole) * day + moonContribution[channel]) * Math.fround(intensity);
        });
        return [...color, 1];
    };
    if (moonDiscSize === null) return evaluate(moonColor);
    const min = evaluate(moonColor.min), max = evaluate(moonColor.max);
    return min.every((value, channel) => value === max[channel]) ? min : { min, max };
}
