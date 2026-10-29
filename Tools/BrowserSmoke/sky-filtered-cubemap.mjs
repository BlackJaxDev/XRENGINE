import { skyCheck } from './sky-check.mjs';

const axes = [[0, 0, -1], [1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1]];
const edges = [[1, 1, 0], [1, -1, 0], [-1, 1, 0], [-1, -1, 0],
    [1, 0, 1], [1, 0, -1], [-1, 0, 1], [-1, 0, -1],
    [0, 1, 1], [0, 1, -1], [0, -1, 1], [0, -1, -1]];
const mipLevel = sampleCase => sampleCase >= 6 && sampleCase <= 11 || sampleCase >= 25 && sampleCase <= 36 ? 1 : sampleCase === 12 ? 2 : 0;
const isEdge = sampleCase => sampleCase >= 13 && sampleCase <= 39;
const normalize = values => values.map(value => value / Math.hypot(...values));
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
function decode(value) {
    const encoded = value / 255;
    return encoded <= 0.04045 ? encoded / 12.92 : ((encoded + 0.055) / 1.055) ** 2.4;
}
function faceColor(face, level) {
    return (level === 2 ? [112, 144, 192]
        : [32 + face * 28 + level * 20, 180 - face * 20 + level * 10, 64 + face * 16 + level * 24]).map(decode);
}

function reference(sampleCase, x, y, width, height) {
    const level = mipLevel(sampleCase);
    // Uniform final-mip faces qualify selection, not three-face corner weights.
    if (level === 2) return [...faceColor(0, level), 1];
    const forward = normalize(isEdge(sampleCase)
        ? edges[sampleCase <= 36 ? (sampleCase - 13) % 12 : 5]
        : axes[sampleCase < 12 ? sampleCase % 6 : 0]);
    const right = normalize(cross(forward, Math.abs(forward[1]) > 0.95 ? [0, 0, 1] : [0, 1, 0]));
    const up = cross(right, forward);
    const scale = Math.tan((isEdge(sampleCase) ? 10 : 30) * Math.PI / 360);
    const dx = (2 * (x + 0.5) / width - 1) * width / height * scale;
    const dy = (1 - 2 * (y + 0.5) / height) * scale;
    let ray = forward.map((value, axis) => value + right[axis] * dx + up[axis] * dy);
    if (sampleCase === 38) ray = [-ray[2], ray[1], ray[0]];
    const ranked = ray.map((value, axis) => ({ magnitude: Math.abs(value), face: axis * 2 + (value < 0 ? 1 : 0) }))
        .sort((a, b) => b.magnitude - a.magnitude);
    const extent = 4 >> level, border = 1 - 1 / extent;
    if (ranked[2].magnitude / ranked[0].magnitude > border)
        throw new Error('BrowserSmoke.SkyCubeReference: three-face corners are outside this reference.');
    // At a face edge, texel coordinate n*(1+b/a)/2-.5 gives the
    // neighboring face's weight. Decode both faces before interpolation.
    const weight = Math.max(0, (ranked[1].magnitude / ranked[0].magnitude - border) * extent / 2);
    const primary = faceColor(ranked[0].face, level), neighbor = faceColor(ranked[1].face, level);
    const intensity = sampleCase === 37 ? 4 : 1;
    return [...primary.map((value, channel) => (value * (1 - weight) + neighbor[channel] * weight) * intensity), 1];
}

/** Qualifies sRGB cube faces, two-face edge filtering, and authored mip selection. */
export async function skyCubemapFilteredCheck(...args) {
    const directions = ['negative-z', 'positive-x', 'negative-x', 'positive-y', 'negative-y', 'positive-z'];
    const pairs = ['px-py', 'px-ny', 'nx-py', 'nx-ny', 'px-pz', 'px-nz', 'nx-pz', 'nx-nz', 'py-pz', 'py-nz', 'ny-pz', 'ny-nz'];
    await skyCheck(...args, {
        checkName: 'engine-sky-cubemap-filtered', reportKey: 'skyCubemapFiltered',
        probe: 'sky-cube-filtered', capturePrefix: 'engine-sky-cube-filtered',
        scope: 'Published canonical cube asset loading, sRGB decode before two-face cube filtering, all twelve edges at two authored mip levels, and uniform final mip selection. Three-face corner weights, implicit/fractional LOD, and automatic mips remain unqualified.',
        names: [...directions, ...directions.map(name => `mip-1-${name}`), 'mip-2',
            ...pairs.map(name => `edge-${name}`), ...pairs.map(name => `mip-1-edge-${name}`),
            'edge-hdr-intensity', 'edge-sky-rotation', 'edge-translated', 'restored'],
        cookedAsset: { path: '/game/SkyCubeFiltered.asset', nativeBytes: 504 },
        semantic: 'SkyboxCubemap', artifact: 'engine-skybox-cubemap', reference,
        cubeStorage: { label: 'SkyCubeFiltered', extent: 4, format: 'rgba8unorm-srgb', mipLevels: 3 },
        foregroundCases: [],
        expectedState: sampleCase => ({ mode: 'Texture', projection: 'Cubemap', intensity: sampleCase === 37 ? 4 : 1,
            rotation: sampleCase === 38 ? 90 : 0, verticalFieldOfView: isEdge(sampleCase) ? 10 : 30,
            textureMinLod: mipLevel(sampleCase), textureMaxLod: mipLevel(sampleCase) }),
        replacesMaterial: () => false,
        replacesBindings: (previous, current) => mipLevel(previous) !== mipLevel(current),
    });
}
