import { skyCheck } from './sky-check.mjs';
import { skyImageCoordinates } from './sky-image-coordinates.mjs';

const mipLevel = sampleCase => sampleCase === 10 || sampleCase === 13 ? 1 : sampleCase === 11 ? 2 : sampleCase === 12 ? 3 : 0;
function decodeSrgb(value) {
    const normalized = value / 255;
    return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
}

function reference(projection, sampleCase, x, y, width, height, foreground) {
    if (foreground) return [0.4 * 0.33, 0.2 * 0.33, 0.1 * 0.33, 0.7];
    const pose = sampleCase <= 9 ? sampleCase : sampleCase === 13 ? 7 : 0;
    const [u, v] = skyImageCoordinates(projection, pose, x, y, width, height);
    const level = mipLevel(sampleCase), columns = 8 >> level;
    const rows = Math.max(1, (projection === 'Equirectangular' ? 4 : 8) >> level);
    const px = u * columns - 0.5, py = v * rows - 0.5;
    const left = Math.floor(px), top = Math.floor(py), fx = px - left, fy = py - top;
    const color = [0, 0, 0];
    for (let dy = 0; dy < 2; dy++)
        for (let dx = 0; dx < 2; dx++) {
            const column = projection === 'Equirectangular'
                ? ((left + dx) % columns + columns) % columns
                : Math.max(0, Math.min(columns - 1, left + dx));
            const row = Math.max(0, Math.min(rows - 1, top + dy));
            const weight = (dx ? fx : 1 - fx) * (dy ? fy : 1 - fy);
            const bytes = [32 + column * 24 + level * 40, 40 + row * 20 + level * 24,
                80 + column * 6 + row * 10 + level * 24];
            // Decode each texel before filtering. Encoded-space interpolation
            // would produce a different value at the panorama wrap seam.
            for (let channel = 0; channel < 3; channel++) color[channel] += decodeSrgb(bytes[channel]) * weight;
        }
    const intensity = sampleCase === 13 ? 4 : sampleCase === 8 ? 2 : 1;
    return [...color.map(value => value * intensity), 1];
}

async function filteredCheck(args, projection) {
    const name = projection.toLowerCase();
    await skyCheck(...args, {
        checkName: `engine-sky-${name}-filtered`, reportKey: `sky${projection}Filtered`,
        probe: `sky-${name}-filtered`, capturePrefix: `engine-sky-${name}-filtered`,
        scope: 'sRGB decode before bilinear filtering, authored wrap/clamp behavior, and four explicit integer mip levels. Implicit LOD, trilinear blends, and seamless octahedral edge remapping remain unqualified.',
        names: ['negative-z', 'positive-x', 'negative-x', 'positive-y', 'negative-y', 'positive-z',
            'translated', 'sky-rotation', 'hdr-intensity', 'restored', 'mip-1', 'mip-2', 'mip-3', 'mip-1-hdr-rotation', 'mip-restored'],
        semantic: `Skybox${projection}`, artifact: `engine-skybox-${name}`,
        imageExtent: [8, projection === 'Equirectangular' ? 4 : 8], imageFormat: 'rgba8unorm-srgb', imageMipLevels: 4,
        reference: (...values) => reference(projection, ...values),
        foregroundCases: [0, 7, 8, 9, 10, 11, 12, 13, 14],
        expectedState: sampleCase => ({ mode: 'Texture', projection,
            intensity: sampleCase === 13 ? 4 : sampleCase === 8 ? 2 : 1,
            rotation: sampleCase === 7 || sampleCase === 13 ? 90 : 0,
            textureMinLod: mipLevel(sampleCase), textureMaxLod: mipLevel(sampleCase) }),
        replacesMaterial: () => false,
        replacesBindings: (previous, current) => mipLevel(previous) !== mipLevel(current),
    });
}

/** Qualifies panorama filtering and authored sRGB mip storage. */
export async function skyEquirectangularFilteredCheck(...args) { await filteredCheck(args, 'Equirectangular'); }

/** Qualifies octahedral filtering and authored sRGB mip storage. */
export async function skyOctahedralFilteredCheck(...args) { await filteredCheck(args, 'Octahedral'); }
