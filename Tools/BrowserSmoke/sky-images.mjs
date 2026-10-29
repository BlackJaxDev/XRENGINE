import { skyCheck } from './sky-check.mjs';

// Derive authored texel coordinates from camera rays. These values never enter
// the renderer. Pixel centers avoid an exact longitude singularity at the poles.
function reference(projection, sampleCase, x, y, width, height, foreground) {
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
    let u, v;
    if (projection === 'Equirectangular') {
        u = Math.atan2(c, a) / (2 * Math.PI) + 0.5;
        u -= Math.floor(u);
        v = 0.5 - Math.asin(b / Math.hypot(a, b, c)) / Math.PI;
    } else {
        const length = Math.abs(a) + Math.abs(b) + Math.abs(c);
        const horizontal = a / length, vertical = c / length;
        u = horizontal; v = vertical;
        if (b < 0) {
            u = (1 - Math.abs(vertical)) * (horizontal >= 0 ? 1 : -1);
            v = (1 - Math.abs(horizontal)) * (vertical >= 0 ? 1 : -1);
        }
        u = (u + 1) / 2; v = (v + 1) / 2;
    }
    const rows = projection === 'Equirectangular' ? 4 : 8;
    const column = Math.max(0, Math.min(7, Math.floor(u * 8)));
    const row = Math.max(0, Math.min(rows - 1, Math.floor(v * rows)));
    const intensity = sampleCase === 8 ? 2 : 1;
    return [(column + 1) * 0.25 * intensity, (row + 0.5) * 0.125 * intensity,
        (0.25 + (column + row) * 0.0625) * intensity, 1];
}

async function imageCheck(args, projection) {
    const name = projection.toLowerCase();
    await skyCheck(...args, {
        checkName: `engine-sky-${name}`, reportKey: `sky${projection}`, probe: `sky-${name}`, capturePrefix: `engine-sky-${name}`,
        scope: `${projection} HDR image coordinates with nearest sampling; filtered seams, mip chains, and sRGB image decoding remain unqualified.`,
        names: ['negative-z', 'positive-x', 'negative-x', 'positive-y', 'negative-y', 'positive-z',
            'translated', 'sky-rotation', 'hdr-intensity', 'restored'],
        semantic: `Skybox${projection}`, artifact: `engine-skybox-${name}`,
        imageExtent: [8, projection === 'Equirectangular' ? 4 : 8],
        reference: (...values) => reference(projection, ...values),
        foregroundCases: [0, 7, 8, 9],
        expectedState: sampleCase => ({ mode: 'Texture', projection, intensity: sampleCase === 8 ? 2 : 1,
            rotation: sampleCase === 7 ? 90 : 0 }),
        replacesMaterial: () => false,
    });
}

/** Qualifies panorama coordinates, HDR values, and texture lifetime through the shared renderer. */
export async function skyEquirectangularCheck(...args) { await imageCheck(args, 'Equirectangular'); }

/** Qualifies both octahedral hemispheres, HDR values, and texture lifetime through the shared renderer. */
export async function skyOctahedralCheck(...args) { await imageCheck(args, 'Octahedral'); }
