import { skyCheck } from './sky-check.mjs';

const fieldOfView = sampleCase => sampleCase === 0 ? 90 : [2, 5, 6].includes(sampleCase) ? 160 : [3, 7].includes(sampleCase) ? 170 : 140;
const limits = sampleCase => [sampleCase === 8 ? 2 : 0, sampleCase === 7 ? 1 : 8];
function linear(value) {
    const encoded = value / 255;
    return encoded <= 0.04045 ? encoded / 12.92 : ((encoded + 0.055) / 1.055) ** 2.4;
}
function reference(sampleCase, x, y, width, height) {
    const [minimum, maximum] = limits(sampleCase);
    const footprint = 256 * Math.tan(fieldOfView(sampleCase) * Math.PI / 360) / height;
    // On the selected face axis, the projected UV footprint is constant.
    // Concavity bounds log2 between its half-step secant and the ideal value.
    // The lower bound also covers the inspected SwiftShader sampler approximation.
    // This is a software qualification interval, not a hardware precision promise.
    const squared = footprint * footprint;
    const exponent = Math.floor(Math.log2(squared));
    const lowerLod = 0.5 * (exponent + squared / 2 ** exponent - 1);
    const upperLod = Math.log2(footprint);
    const sample = lod => {
        const level = Math.max(minimum, Math.min(maximum, lod));
        const lower = Math.floor(level), upper = Math.min(8, lower + 1), weight = level - lower;
        const color = mip => [[32, 224, 64], [224, 32, 192], [64, 192, 224], [192, 64, 32]][mip % 4].map(linear);
        const a = color(lower), b = color(upper), intensity = sampleCase === 6 ? 4 : 1;
        return [...a.map((value, channel) => (value * (1 - weight) + b[channel] * weight) * intensity), 1];
    };
    const a = sample(lowerLod), b = sample(upperLod);
    return { min: a.map((value, channel) => Math.min(value, b[channel])),
        max: a.map((value, channel) => Math.max(value, b[channel])) };
}

/** Checks implicit cube LOD and decoded-linear mip blends with independent bounds. */
export async function skyCubemapImplicitCheck(...args) {
    await skyCheck(...args, {
        checkName: 'engine-sky-cubemap-implicit', reportKey: 'skyCubemapImplicit',
        probe: 'sky-cube-implicit', capturePrefix: 'engine-sky-cube-implicit',
        scope: 'Saved cube loading, bounded implicit mip selection and trilinear sRGB filtering near face centers; authored LOD clamps, camera footprint, intensity, resize and restart. Edge derivatives, anisotropy and hardware precision remain separate.',
        names: ['magnification', 'fractional-0', 'fractional-1', 'fractional-2', 'positive-x', 'translated', 'hdr-intensity', 'maximum-clamp', 'minimum-clamp', 'restored'],
        semantic: 'SkyboxCubemap', artifact: 'engine-skybox-cubemap',
        cookedAsset: { path: '/game/SkyCubeImplicit.asset', nativeBytes: 2097144 },
        cubeStorage: { label: 'SkyCubeImplicit', extent: 256, format: 'rgba8unorm-srgb', mipLevels: 9 },
        positions: [[0.498, 0.498], [0.502, 0.498], [0.498, 0.502], [0.502, 0.502], [0.5, 0.5]],
        resizeCases: [1, 2, 3], foregroundCases: [], reference, allowReferenceBounds: true,
        expectedState: sampleCase => ({ mode: 'Texture', projection: 'Cubemap',
            intensity: sampleCase === 6 ? 4 : 1, verticalFieldOfView: fieldOfView(sampleCase),
            textureMinLod: limits(sampleCase)[0], textureMaxLod: limits(sampleCase)[1] }),
        replacesMaterial: () => false,
        replacesBindings: (previous, current) => String(limits(previous)) !== String(limits(current)),
    });
}
