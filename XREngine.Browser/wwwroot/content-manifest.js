/** Limits apply before allocation or GPU upload. Content is a cooked, same-origin package. */
export const CONTENT_LIMITS = Object.freeze({
    manifestBytes: 1024 * 1024, assets: 4096, dependencies: 64, depth: 32,
    payloadBytes: 4 * 1024 * 1024, jsonBytes: 1024 * 1024,
    selectedBytes: 64 * 1024 * 1024, concurrency: 3, stagingBytes: 12 * 1024 * 1024,
    requestMilliseconds: 30000, attempts: 3,
});

export const CONTENT_OPTIONAL_FEATURES = Object.freeze([
    'texture-compression-astc', 'texture-compression-etc2',
]);

const formats = Object.freeze({
    'rgba8unorm': { block: 1, bytes: 4, feature: null, srgb: false },
    'rgba8unorm-srgb': { block: 1, bytes: 4, feature: null, srgb: true },
    'astc-4x4-unorm': { block: 4, bytes: 16, feature: 'texture-compression-astc', srgb: false },
    'astc-4x4-unorm-srgb': { block: 4, bytes: 16, feature: 'texture-compression-astc', srgb: true },
    'etc2-rgba8unorm': { block: 4, bytes: 16, feature: 'texture-compression-etc2', srgb: false },
    'etc2-rgba8unorm-srgb': { block: 4, bytes: 16, feature: 'texture-compression-etc2', srgb: true },
});

function reject(message) { throw new Error(`Cooked content: ${message}`); }
function keys(value, expected, label) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) reject(`${label} must be an object.`);
    const actual = Object.keys(value);
    if (actual.length !== expected.length || actual.some(key => !expected.includes(key)))
        reject(`${label} has missing or unknown fields.`);
}
function integer(value, min, max, label) {
    if (!Number.isSafeInteger(value) || value < min || value > max) reject(`${label} is outside its supported range.`);
}
function id(value) {
    if (typeof value !== 'string' || value.length > 64 || /\s/.test(value)
        || !/^[a-z][a-z0-9._-]{0,63}$/.test(value) || value.includes('..')) reject('Invalid logical asset ID.');
    return value;
}
function ids(value, max, label) {
    if (!Array.isArray(value) || value.length > max) reject(`${label} exceeds its array limit.`);
    const unique = new Set();
    for (const item of value) {
        id(item);
        if (unique.has(item)) reject(`${label} repeats asset ${item}.`);
        unique.add(item);
    }
}

/** URL policy also rejects normalization tricks before URL parsing can erase them. */
export function contentManifestUrl(value, base = globalThis.location.href) {
    if (typeof value === 'string' && value.startsWith('./')) value = value.slice(2);
    if (typeof value !== 'string' || !value || /[\\%?#\s]/.test(value)
        || value.split('/').some(part => part === '.' || part === '..')) reject('Manifest URL contains disallowed path syntax.');
    const url = new URL(value, base);
    const origin = new URL(base);
    const local = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
    if (url.origin !== origin.origin || url.username || url.password
        || !(url.protocol === 'https:' || (url.protocol === 'http:' && local)))
        reject('Content requires same-origin HTTPS or localhost HTTP, without credentials.');
    return url.href;
}

/** Validates the entire graph, including variants the current device will not select. */
export function validateContentManifest(manifest, manifestUrl, capabilities) {
    keys(manifest, ['schema', 'profile', 'toolchain', 'entrypoints', 'streamed', 'assets'], 'manifest');
    if (manifest.schema !== 1 || manifest.profile !== 'browser-forward-v1'
        || manifest.toolchain !== 'xrengine-browser-content-1') reject('Unsupported schema, profile or toolchain.');
    if (!Array.isArray(manifest.assets) || !manifest.assets.length || manifest.assets.length > CONTENT_LIMITS.assets)
        reject('Asset count is outside the supported range.');
    ids(manifest.entrypoints, CONTENT_LIMITS.assets, 'entrypoints');
    ids(manifest.streamed, CONTENT_LIMITS.assets, 'streamed');
    if (!manifest.entrypoints.length) reject('At least one essential entrypoint is required.');
    const features = new Set(capabilities?.features ?? []);
    const assets = new Map();
    const selected = new Map();
    const payloadUrls = new Map();
    const payloadSizes = new Map();
    let selectedBytes = 0;
    const rootUrl = contentManifestUrl(manifestUrl);
    for (const asset of manifest.assets) {
        keys(asset, ['id', 'kind', 'dependencies', 'variants'], 'asset');
        id(asset.id);
        if (assets.has(asset.id)) reject(`Duplicate asset ${asset.id}.`);
        if (!['mesh', 'texture', 'material', 'scene'].includes(asset.kind)) reject(`${asset.id}: unsupported kind.`);
        ids(asset.dependencies, CONTENT_LIMITS.dependencies, `${asset.id} dependencies`);
        if (!Array.isArray(asset.variants) || !asset.variants.length || asset.variants.length > 8)
            reject(`${asset.id}: variant count is outside the supported range.`);
        let fallback = null;
        let chosen = null;
        const hashes = new Set();
        for (const variant of asset.variants) {
            const fields = ['hash', 'url', 'bytes', 'encoding', 'requiredFeatures'];
            if (asset.kind === 'texture') fields.push('width', 'height', 'format', 'mipByteLengths', 'normalConvention', 'alphaMode');
            keys(variant, fields, `${asset.id} variant`);
            if (typeof variant.hash !== 'string' || variant.hash.length !== 64 || !/^[a-f0-9]{64}$/.test(variant.hash)
                || variant.url !== `payloads/${variant.hash}.bin`) reject(`${asset.id}: invalid content-addressed payload URL.`);
            if (hashes.has(variant.hash)) reject(`${asset.id}: duplicate variant hash.`);
            hashes.add(variant.hash);
            integer(variant.bytes, 1, asset.kind === 'texture' ? CONTENT_LIMITS.payloadBytes : CONTENT_LIMITS.jsonBytes, `${asset.id} bytes`);
            if (variant.encoding !== (asset.kind === 'texture' ? 'raw' : 'json')) reject(`${asset.id}: unsupported encoding.`);
            if (!Array.isArray(variant.requiredFeatures) || variant.requiredFeatures.length > 1
                || variant.requiredFeatures.some(feature => !CONTENT_OPTIONAL_FEATURES.includes(feature)))
                reject(`${asset.id}: unsupported required feature.`);
            if (asset.kind === 'texture') {
                const format = Object.hasOwn(formats, variant.format) ? formats[variant.format] : null;
                if (!format) reject(`${asset.id}: unsupported texture format.`);
                integer(variant.width, 1, 8192, `${asset.id} width`);
                integer(variant.height, 1, 8192, `${asset.id} height`);
                if (format.block === 4 && (variant.width % 4 || variant.height % 4)) reject(`${asset.id}: compressed base extent must be block aligned.`);
                if (!['none', 'tangent-y-positive'].includes(variant.normalConvention)
                    || (variant.normalConvention !== 'none' && format.srgb)
                    || variant.alphaMode !== 'straight') reject(`${asset.id}: unsupported normal or alpha convention.`);
                const expectedFeatures = format.feature ? [format.feature] : [];
                if (variant.requiredFeatures.length !== expectedFeatures.length
                    || variant.requiredFeatures.some((feature, index) => feature !== expectedFeatures[index]))
                    reject(`${asset.id}: format and required features disagree.`);
                const levels = Math.floor(Math.log2(Math.max(variant.width, variant.height))) + 1;
                if (!Array.isArray(variant.mipByteLengths) || variant.mipByteLengths.length !== levels)
                    reject(`${asset.id}: a complete mip chain is required.`);
                let total = 0;
                for (let level = 0; level < levels; level++) {
                    const width = Math.max(1, Math.floor(variant.width / 2 ** level));
                    const height = Math.max(1, Math.floor(variant.height / 2 ** level));
                    const expected = Math.ceil(width / format.block) * Math.ceil(height / format.block) * format.bytes;
                    if (variant.mipByteLengths[level] !== expected) reject(`${asset.id}: invalid mip ${level} byte length.`);
                    total += expected;
                }
                if (total !== variant.bytes) reject(`${asset.id}: mip lengths do not match payload size.`);
                if (!format.feature) fallback = variant;
            } else if (variant.requiredFeatures.length || asset.variants.length !== 1) {
                reject(`${asset.id}: JSON assets require one feature-independent variant.`);
            }
            if (!chosen && variant.requiredFeatures.every(feature => features.has(feature))) chosen = variant;
            if (payloadSizes.has(variant.hash) && payloadSizes.get(variant.hash) !== variant.bytes)
                reject(`${asset.id}: a payload hash has inconsistent byte lengths.`);
            payloadSizes.set(variant.hash, variant.bytes);
            payloadUrls.set(variant, new URL(variant.url, rootUrl).href);
        }
        if (asset.kind === 'texture') {
            if (!fallback) reject(`${asset.id}: an RGBA8 fallback is required.`);
            for (const variant of asset.variants) {
                if (variant.width !== fallback.width || variant.height !== fallback.height
                    || variant.mipByteLengths.length !== fallback.mipByteLengths.length
                    || formats[variant.format].srgb !== formats[fallback.format].srgb
                    || variant.normalConvention !== fallback.normalConvention || variant.alphaMode !== fallback.alphaMode)
                    reject(`${asset.id}: texture variants disagree on image semantics.`);
            }
        }
        if (!chosen) reject(`${asset.id}: no supported variant is available.`);
        selectedBytes += chosen.bytes;
        if (selectedBytes > CONTENT_LIMITS.selectedBytes) reject('Selected payloads exceed the package byte budget.');
        assets.set(asset.id, asset);
        selected.set(asset.id, chosen);
    }
    for (const root of [...manifest.entrypoints, ...manifest.streamed]) {
        if (assets.get(root)?.kind !== 'scene') reject(`${root}: package roots must identify scene assets.`);
    }
    const marks = new Map();
    const depths = new Map();
    function visit(assetId, ancestors = 1) {
        if (ancestors > CONTENT_LIMITS.depth) reject(`${assetId}: dependency depth exceeds the limit.`);
        const asset = assets.get(assetId);
        if (!asset) reject(`Missing dependency or root ${assetId}.`);
        if (marks.get(assetId) === 1) reject(`Dependency cycle at ${assetId}.`);
        if (marks.get(assetId) === 2) return depths.get(assetId);
        marks.set(assetId, 1);
        let depth = 1;
        for (const dependency of asset.dependencies) depth = Math.max(depth, visit(dependency, ancestors + 1) + 1);
        if (depth > CONTENT_LIMITS.depth) reject(`${assetId}: dependency depth exceeds the limit.`);
        marks.set(assetId, 2);
        depths.set(assetId, depth);
        return depth;
    }
    for (const asset of manifest.assets) visit(asset.id);
    const scheduled = new Set();
    function order(roots) {
        const result = [];
        function append(assetId) {
            const asset = assets.get(assetId);
            if (!asset) reject(`Missing root ${assetId}.`);
            if (scheduled.has(assetId)) return;
            scheduled.add(assetId);
            for (const dependency of asset.dependencies) append(dependency);
            result.push(asset);
        }
        for (const root of roots) append(root);
        return result;
    }
    const essential = order(manifest.entrypoints);
    const streamed = order(manifest.streamed);
    for (const asset of manifest.assets) {
        Object.freeze(asset.dependencies);
        for (const variant of asset.variants) {
            Object.freeze(variant.requiredFeatures);
            if (variant.mipByteLengths) Object.freeze(variant.mipByteLengths);
            Object.freeze(variant);
        }
        Object.freeze(asset.variants);
        Object.freeze(asset);
    }
    Object.freeze(manifest.assets);
    Object.freeze(manifest.entrypoints);
    Object.freeze(manifest.streamed);
    Object.freeze(manifest);
    return { manifest, selected, payloadUrls, essential, streamed, selectedBytes };
}
