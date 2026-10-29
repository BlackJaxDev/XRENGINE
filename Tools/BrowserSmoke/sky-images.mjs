import { skyCheck } from './sky-check.mjs';
import { skyImageCoordinates } from './sky-image-coordinates.mjs';

// Derive authored texel coordinates from camera rays. These values never enter
// the renderer. Pixel centers avoid an exact longitude singularity at the poles.
function reference(projection, sampleCase, x, y, width, height, foreground) {
    if (foreground) return [0.4 * 0.33, 0.2 * 0.33, 0.1 * 0.33, 0.7];
    const [u, v] = skyImageCoordinates(projection, sampleCase, x, y, width, height);
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
        scope: `Published canonical ${projection} HDR image loading and coordinates with nearest sampling; filtered seams, mip chains, and sRGB decoding remain outside this profile.`,
        names: ['negative-z', 'positive-x', 'negative-x', 'positive-y', 'negative-y', 'positive-z',
            'translated', 'sky-rotation', 'hdr-intensity', 'restored'],
        semantic: `Skybox${projection}`, artifact: `engine-skybox-${name}`,
        cookedAsset: { path: `/game/Sky${projection}Hdr.asset`, nativeBytes: projection === 'Equirectangular' ? 256 : 512 },
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
