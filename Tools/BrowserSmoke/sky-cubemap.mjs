import { skyCheck } from './sky-check.mjs';

// Compute face coordinates from independent camera rays. Face RGB encodes its
// index, texel column, and texel row; authored texture alpha must not affect sky alpha.
function reference(sampleCase, x, y, width, height, foreground) {
    if (foreground) return [0.4 * 0.33, 0.2 * 0.33, 0.1 * 0.33, 0.7];
    const dx = (2 * (x + 0.5) / width - 1) * width / height;
    const dy = 1 - 2 * (y + 0.5) / height;
    let direction = [dx, dy, -1];
    switch (sampleCase) {
        case 1: case 7: direction = [1, dy, dx]; break;
        case 2: direction = [-1, dy, -dx]; break;
        case 3: direction = [dx, 1, dy]; break;
        case 4: direction = [dx, -1, -dy]; break;
        case 5: direction = [-dx, dy, 1]; break;
    }
    const [a, b, c] = direction;
    const major = Math.max(Math.abs(a), Math.abs(b), Math.abs(c));
    let face, sc, tc;
    if (Math.abs(a) === major) {
        face = a > 0 ? 0 : 1; sc = a > 0 ? -c : c; tc = -b;
    } else if (Math.abs(b) === major) {
        face = b > 0 ? 2 : 3; sc = a; tc = b > 0 ? c : -c;
    } else {
        face = c > 0 ? 4 : 5; sc = c > 0 ? a : -a; tc = -b;
    }
    const column = Math.max(0, Math.min(3, Math.floor((sc / major + 1) * 2)));
    const row = Math.max(0, Math.min(3, Math.floor((tc / major + 1) * 2)));
    const intensity = sampleCase === 8 ? 2 : 1;
    return [(face + 1) * 0.25 * intensity, (column + 0.5) * 0.25 * intensity, (row + 0.5) * 0.25 * intensity, 1];
}

/** Qualifies cube face order, orientation, HDR radiance, and retained ownership. */
export async function skyCubemapCheck(...args) {
    await skyCheck(...args, {
        checkName: 'engine-sky-cubemap', reportKey: 'skyCubemap', probe: 'sky-cube', capturePrefix: 'engine-sky-cube',
        scope: 'Published canonical cube asset loading and six authored RGBA16F faces with nearest sampling; filtered seams, mip chains, and other image projections remain unqualified.',
        names: ['negative-z', 'positive-x', 'negative-x', 'positive-y', 'negative-y', 'positive-z',
            'translated', 'sky-rotation', 'hdr-intensity', 'restored'],
        cookedAsset: { path: '/game/SkyCubeHdr.asset', nativeBytes: 768 },
        cubeStorage: { label: 'SkyCubeHdr', extent: 4, format: 'rgba16float', mipLevels: 1 },
        semantic: 'SkyboxCubemap', artifact: 'engine-skybox-cubemap', reference,
        foregroundCases: [0, 7, 8, 9],
        expectedState: sampleCase => ({ mode: 'Texture', projection: 'Cubemap', intensity: sampleCase === 8 ? 2 : 1,
            rotation: sampleCase === 7 ? 90 : 0 }),
        replacesMaterial: () => false,
    });
}
