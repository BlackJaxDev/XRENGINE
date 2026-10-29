const clamp = value => Math.max(0, Math.min(1, value));
const dot = (a, b) => a.reduce((sum, value, index) => sum + value * b[index], 0);
const normalize = values => values.map(value => value / Math.hypot(...values));
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
function smooth(a, b, value) {
    const t = clamp((value - a) / (b - a));
    return t * t * (3 - 2 * t);
}

/** Bounds the noisy moon surface and evaluates the analytic corona exactly. */
export function moonRadianceBounds(direction, moon, sun, nightWeight, discSize) {
    const d = dot(direction, moon), cosine = Math.max(d, 0);
    const onDisc = d > discSize;
    const corona = cosine ** 96 * (onDisc ? 0.4 : 0.35) + cosine ** 24 * (onDisc ? 0.06 : 0.05);
    const glow = [0.74, 0.79, 0.94].map(value => value * corona);
    if (!onDisc || nightWeight === 0) {
        const color = glow.map(value => value * nightWeight);
        return { min: color, max: color };
    }
    const up = Math.abs(moon[1]) < 0.95 ? [0, 1, 0] : [1, 0, 0];
    const tangent = normalize(cross(up, moon)), bitangent = cross(moon, tangent);
    const local = [dot(direction, tangent) / Math.max(d, 0.001), dot(direction, bitangent) / Math.max(d, 0.001)];
    const radius = Math.sqrt(Math.max(1 - discSize * discSize, 0.000001)) * 1.15;
    const uv = local.map(value => value / radius), rn = clamp(dot(uv, uv));
    const normal = normalize(moon.map((value, axis) => value * Math.sqrt(Math.max(1 - rn, 0)) +
        tangent[axis] * uv[0] + bitangent[axis] * uv[1]));
    const phase = clamp(dot(normal, sun)), edge = smooth(1, 0.85, rn);
    // Value noise lies in [0,1]. Six amplitudes are 0.5 * 0.55^octave.
    // The crater lerp is unclamped, so its upper bound is slightly above one.
    const detailMaximum = 0.5 * (1 - 0.55 ** 6) / (1 - 0.55);
    const craterMinimum = 0.72 + 0.28 * 0.5;
    const craterMaximum = 0.72 + 0.28 * (detailMaximum * 0.5 + 0.5);
    const scale = (0.06 + phase * 1.15) * edge;
    const bound = (albedo, craters) => glow.map((value, channel) =>
        (value + albedo[channel] * craters * scale) * nightWeight);
    return { min: bound([0.45, 0.49, 0.58], craterMinimum),
        max: bound([0.82, 0.83, 0.86], craterMaximum) };
}
