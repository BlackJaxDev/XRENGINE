const manifestUrl = new URL('./shaders/manifest.json', import.meta.url);
const digestName = /^[0-9a-f]{64}$/;
const supportedLimits = new Set([
    'maxVertexAttributes', 'maxBindGroups', 'maxBindingsPerBindGroup',
    'maxUniformBufferBindingSize', 'maxDynamicUniformBuffersPerPipelineLayout',
]);

const expectedLayout = {
    vertexStride: 20, positionOffset: 0, uvOffset: 12, transformBytes: 64, materialBytes: 16,
    bindings: [
        { group: 0, binding: 0, kind: 'uniform', visibility: 'vertex', bytes: 64, dynamic: true },
        { group: 1, binding: 0, kind: 'uniform', visibility: 'fragment', bytes: 16, dynamic: false },
        { group: 1, binding: 1, kind: 'texture-2d-float', visibility: 'fragment' },
        { group: 1, binding: 2, kind: 'filtering-sampler', visibility: 'fragment' },
    ],
};
const expectedPipeline = {
    topology: 'triangle-list', cullMode: 'none', depthFormat: 'depth24plus',
    depthWrite: true, depthCompare: 'less', blend: false, sampleCount: 1,
};

function fail(where, message) {
    throw new Error(`Shader artifact ${where}: ${message}`);
}

function exact(value, expected) {
    if (Array.isArray(expected))
        return Array.isArray(value) && value.length === expected.length
            && expected.every((item, index) => exact(value[index], item));
    if (expected && typeof expected === 'object')
        return value !== null && typeof value === 'object' && !Array.isArray(value)
            && Object.keys(value).length === Object.keys(expected).length
            && Object.keys(expected).every(key => Object.hasOwn(value, key) && exact(value[key], expected[key]));
    return value === expected;
}

function fields(value, keys, where) {
    if (!value || typeof value !== 'object' || Array.isArray(value)
        || Object.keys(value).length !== keys.length
        || !keys.every(key => Object.hasOwn(value, key)))
        fail(where, `expected fields ${keys.join(', ')}.`);
}

function hash(value, where) {
    if (typeof value !== 'string' || !digestName.test(value))
        fail(where, 'expected a lowercase SHA-256 digest.');
}

function artifactUrl(filename, where) {
    const url = new URL(filename, manifestUrl);
    if (url.origin !== manifestUrl.origin || url.pathname !== new URL(`./${filename}`, manifestUrl).pathname
        || url.search || url.hash)
        fail(where, 'must resolve to a file beside the manifest.');
    return url;
}

async function readBounded(url, maximum, signal, cache, where) {
    const response = await fetch(url, { signal, cache, redirect: 'error' });
    if (!response.ok) fail(where, `HTTP ${response.status}.`);
    if (response.url !== url.href)
        fail(where, `redirected to ${response.url || 'an opaque response'}.`);
    const header = response.headers.get('content-length');
    if (header !== null && /^\d+$/.test(header) && Number(header) > maximum) {
        await response.body?.cancel();
        fail(where, `exceeds ${maximum} bytes.`);
    }
    if (!response.body) fail(where, 'a readable response body is required.');
    const reader = response.body.getReader();
    const chunks = [];
    let length = 0;
    try {
        while (true) {
            const { done, value } = await reader.read();
            if (signal?.aborted) throw new DOMException('Shader artifact loading was canceled.', 'AbortError');
            if (done) break;
            length += value.byteLength;
            if (length > maximum) fail(where, `exceeds ${maximum} bytes.`);
            chunks.push(value);
        }
    } finally {
        reader.releaseLock();
        if (length > maximum || signal?.aborted) await response.body.cancel().catch(() => {});
    }
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) {
        bytes.set(chunk, offset);
        offset += chunk.byteLength;
    }
    return bytes;
}

function decode(bytes, where) {
    try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
    catch { fail(where, 'contains invalid UTF-8.'); }
}

function parse(bytes, where) {
    try { return JSON.parse(decode(bytes, where)); }
    catch (error) {
        if (error.message.startsWith('Shader artifact ')) throw error;
        fail(where, `invalid JSON: ${error.message}`);
    }
}

async function verifyDigest(bytes, expected, where) {
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
    const actual = Array.from(digest, byte => byte.toString(16).padStart(2, '0')).join('');
    if (actual !== expected) fail(where, `SHA-256 mismatch (expected ${expected}, found ${actual}).`);
}

function validateDescriptor(descriptor, identity) {
    fields(descriptor, ['schemaVersion', 'name', 'target', 'sourceLanguage', 'compilerIdentity',
        'semanticSchemaIdentity', 'matrixLayout', 'entryPoints', 'defines', 'specialization',
        'requiredFeatures', 'requiredLimits', 'source', 'dependencies', 'layout', 'pipeline'], identity);
    for (const [key, value] of Object.entries({
        schemaVersion: 1, name: 'browser-unlit', target: 'WebGPUWgsl', sourceLanguage: 'WGSL',
        compilerIdentity: 'xrengine-wgsl-packager/1', semanticSchemaIdentity: 'xrengine.browser.mesh.v1',
        matrixLayout: 'column-major', entryPoints: { vertex: 'vertexMain', fragment: 'fragmentMain' },
        defines: [], specialization: {}, layout: expectedLayout, pipeline: expectedPipeline,
    })) {
        if (!exact(descriptor[key], value)) fail(identity, `unsupported ${key}.`);
    }
    if (!Array.isArray(descriptor.requiredFeatures) || descriptor.requiredFeatures.length)
        fail(identity, 'unsupported requiredFeatures; browser-unlit requires none.');
    const limits = descriptor.requiredLimits;
    if (!limits || typeof limits !== 'object' || Array.isArray(limits))
        fail(identity, 'invalid requiredLimits.');
    for (const [name, value] of Object.entries(limits)) {
        if (!supportedLimits.has(name) || !Number.isSafeInteger(value) || value < 0)
            fail(identity, `unsupported required limit ${name}.`);
    }
    for (const [name, minimum] of Object.entries({ maxVertexAttributes: 2, maxBindGroups: 2,
        maxBindingsPerBindGroup: 3, maxUniformBufferBindingSize: 64,
        maxDynamicUniformBuffersPerPipelineLayout: 1 })) {
        if (!Number.isSafeInteger(limits[name]) || limits[name] < minimum || limits[name] > 0x7fffffff)
            fail(identity, `unsupported requiredLimits.${name}.`);
    }
    fields(descriptor.source, ['path', 'sha256', 'byteLength', 'url'], `${identity} source`);
    const source = descriptor.source;
    if (typeof source.path !== 'string' || source.path.length > 240
        || !/^(?:[A-Za-z0-9_-]+\/)*[A-Za-z0-9_.-]+\.wgsl$/.test(source.path)
        || source.path.split('/').some(part => part === '.' || part === '..'))
        fail(identity, 'unsupported source path.');
    hash(source.sha256, `${identity} source`);
    if (!Number.isSafeInteger(source.byteLength) || source.byteLength < 1 || source.byteLength > 1024 * 1024
        || source.url !== `${source.sha256}.wgsl`)
        fail(identity, 'invalid source length or digest filename.');
    if (!exact(descriptor.dependencies, [{ path: source.path, sha256: source.sha256 }]))
        fail(identity, 'unsupported dependencies.');
}

/** Load and verify the one WebGPU browser shader before requesting a device. */
export async function loadBrowserUnlitArtifact(signal) {
    const manifest = parse(await readBounded(manifestUrl, 64 * 1024, signal, 'no-cache', 'manifest'), 'manifest');
    fields(manifest, ['schemaVersion', 'backend', 'packetVersion', 'artifacts'], 'manifest');
    if (manifest.schemaVersion !== 1 || manifest.backend !== 'WebGPU' || manifest.packetVersion !== 2
        || !Array.isArray(manifest.artifacts) || manifest.artifacts.length < 1 || manifest.artifacts.length > 16)
        fail('manifest', 'unsupported schema, backend, packet version, or artifact count.');
    const names = new Set();
    for (const artifact of manifest.artifacts) {
        fields(artifact, ['name', 'descriptor', 'sha256'], 'manifest entry');
        if (typeof artifact.name !== 'string' || !/^[a-z][a-z0-9-]{0,63}$/.test(artifact.name) || names.has(artifact.name))
            fail('manifest', `unsupported or duplicate artifact name ${String(artifact.name)}.`);
        names.add(artifact.name);
        hash(artifact.sha256, 'manifest entry');
        if (artifact.descriptor !== `${artifact.sha256}.shader.json`)
            fail('manifest entry', 'descriptor filename does not match its digest.');
    }
    const entry = manifest.artifacts.find(item => item.name === 'browser-unlit');
    if (!entry) fail('manifest', 'required browser-unlit artifact is missing.');
    const identity = `${entry.name}@${entry.sha256}`;
    const descriptorBytes = await readBounded(artifactUrl(entry.descriptor, identity), 64 * 1024,
        signal, 'force-cache', identity);
    await verifyDigest(descriptorBytes, entry.sha256, identity);
    const descriptor = parse(descriptorBytes, identity);
    validateDescriptor(descriptor, identity);
    const sourceBytes = await readBounded(artifactUrl(descriptor.source.url, identity), 1024 * 1024,
        signal, 'force-cache', `${identity} ${descriptor.source.path}`);
    if (sourceBytes.byteLength !== descriptor.source.byteLength)
        fail(identity, `${descriptor.source.path} byte length mismatch.`);
    await verifyDigest(sourceBytes, descriptor.source.sha256, `${identity} ${descriptor.source.path}`);
    return { source: decode(sourceBytes, `${identity} ${descriptor.source.path}`), descriptor, artifactIdentity: identity };
}

/** Reject unavailable requirements before passing the exact list to requestDevice. */
export function shaderDeviceRequirements(adapter, descriptor, identity) {
    for (const feature of descriptor.requiredFeatures) {
        if (!adapter.features.has(feature)) fail(identity, `adapter lacks feature ${feature}.`);
    }
    for (const [name, value] of Object.entries(descriptor.requiredLimits)) {
        if (!Number.isSafeInteger(adapter.limits[name]) || adapter.limits[name] < value)
            fail(identity, `adapter limit ${name} is ${adapter.limits[name]}, requires ${value}.`);
    }
    return { requiredFeatures: [...descriptor.requiredFeatures], requiredLimits: { ...descriptor.requiredLimits } };
}
