import { parentPort } from 'node:worker_threads';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import { inspectShadowPixels, inspectShadowCaptureImage, shadowReceiverRegions,
    compareShadowReceiverPixels } from './shadow-image-math.mjs';

const require = createRequire(import.meta.url);
let PNG;
try {
    if (require('playwright-core/package.json').version === '1.63.0')
        PNG = require('playwright-core/lib/utilsBundle').PNG;
} catch { /* The message protocol reports the fixed codec failure. */ }

const slots = new Set(['off-initial', 'off-860', 'off-940', 'current']);
const profiles = [[977, 551, 977, 550], [813, 459, 813, 457], [893, 504, 893, 502]];
const images = new Map();
const maximumPngBytes = 8 * 1024 * 1024;
const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
const fail = code => { throw new Error(code); };
const requireImage = (slot, hash) => {
    const image = images.get(slot);
    if (!image || image.hash !== hash) fail('image-reference');
    return image;
};

function decode(request) {
    if (!slots.has(request.slot)) fail('image-slot');
    const profile = profiles.find(value => value[0] === request.width && value[1] === request.height &&
        value[2] === request.canvasWidth && value[3] === request.canvasHeight);
    if (!profile || request.cssWidth !== request.canvasWidth ||
        !(request.png instanceof Uint8Array) || request.png.byteLength > maximumPngBytes || request.png.byteLength < 45)
        fail('png-size');
    const baselineWidth = { 'off-initial': 977, 'off-860': 813, 'off-940': 893 }[request.slot];
    if (baselineWidth && request.width !== baselineWidth) fail('image-slot');
    const bytes = Buffer.from(request.png.buffer, request.png.byteOffset, request.png.byteLength);
    if (!bytes.subarray(0, 8).equals(signature) || bytes.readUInt32BE(8) !== 13 ||
        bytes.toString('ascii', 12, 16) !== 'IHDR' || bytes.readUInt32BE(16) !== request.width ||
        bytes.readUInt32BE(20) !== request.height || bytes[24] !== 8 || ![2, 6].includes(bytes[25]) ||
        bytes[26] !== 0 || bytes[27] !== 0 || bytes[28] !== 0) fail('png-format');
    let offset = 8, chunks = 0, hasData = false, ended = false;
    while (offset < bytes.length) {
        if (++chunks > 1024 || offset + 12 > bytes.length) fail('png-format');
        const length = bytes.readUInt32BE(offset);
        const type = bytes.toString('ascii', offset + 4, offset + 8);
        if (length > bytes.length - offset - 12) fail('png-format');
        if (type === 'IHDR') {
            if (offset !== 8 || length !== 13) fail('png-format');
        } else if (type === 'IDAT') hasData = true;
        else if (type === 'IEND') {
            if (!hasData || length !== 0 || offset + 12 !== bytes.length) fail('png-format');
            ended = true;
        } else fail('png-format');
        offset += length + 12;
    }
    if (!ended) fail('png-format');
    const hash = createHash('sha256').update(bytes).digest('hex');
    if (hash !== request.hash) fail('png-hash');
    let decoded;
    try { decoded = PNG.sync.read(bytes, { checkCRC: true }); }
    catch { fail('png-decode'); }
    if (decoded.width !== request.width || decoded.height !== request.height ||
        decoded.data.length !== request.width * request.height * 4) fail('png-size');
    for (let offset = 3; offset < decoded.data.length; offset += 4)
        if (decoded.data[offset] !== 255) fail('png-opacity');
    const image = { hash, decoded, canvasWidth: request.canvasWidth, canvasHeight: request.canvasHeight };
    images.set(request.slot, image);
    return { hash, pixels: inspectShadowPixels(decoded, request.cssWidth) };
}

parentPort.on('message', request => {
    let result;
    try {
        if (!Number.isFinite(request.deadline) || Date.now() >= request.deadline) fail('deadline');
        if (typeof PNG?.sync?.read !== 'function') fail('codec');
        if (request.operation === 'ready') result = { codec: 'playwright-core@1.63.0/pngjs', retainedImageSlots: 4,
            maximumPngBytes, maximumRetainedRgbaBytes: 4 * 977 * 551 * 4 };
        else if (request.operation === 'inspect') result = decode(request);
        else if (request.operation === 'align') {
            const image = requireImage(request.slot, request.hash);
            const regions = shadowReceiverRegions(image.decoded.width, image.decoded.height, image.canvasWidth, image.canvasHeight);
            const alignment = inspectShadowCaptureImage(image.decoded, regions);
            let comparison = null;
            if (request.reference && alignment.accepted) {
                const baseline = requireImage(request.reference.slot, request.reference.hash);
                if (baseline.decoded.width !== image.decoded.width || baseline.decoded.height !== image.decoded.height ||
                    baseline.canvasWidth !== image.canvasWidth || baseline.canvasHeight !== image.canvasHeight)
                    fail('image-reference');
                comparison = compareShadowReceiverPixels(baseline.decoded, image.decoded, regions);
            }
            result = { hash: image.hash, alignment, comparison };
        } else fail('operation');
        if (Date.now() >= request.deadline) fail('deadline');
        parentPort.postMessage({ id: request.id, result });
    } catch (error) {
        const allowed = ['deadline', 'codec', 'png-size', 'png-format', 'png-hash', 'png-decode',
            'png-opacity', 'image-slot', 'image-reference', 'operation'];
        parentPort.postMessage({ id: request.id, error: allowed.includes(error.message) ? error.message : 'analysis-failed' });
    }
});
