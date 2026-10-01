import { BrowserContentLoader } from './content-loader.js';
import { CONTENT_LIMITS, contentManifestUrl } from './content-manifest.js';

const sources = new Map();
let nextSource = 0;

function requireSource(id) {
    const source = sources.get(id);
    if (!source) throw new Error('AssetSource.StaleSession: the content owner has been disposed.');
    return source;
}

function assetPath(path) {
    if (typeof path !== 'string' || path.length > 1024
        || !/^\/(engine|game)\/[A-Za-z0-9_. /-]+$/.test(path)
        || path.split('/').slice(1).some(part => !part || part === '.' || part === '..'))
        throw new Error(`AssetSource.InvalidPath: '${path}'.`);
    return path;
}

/** Validates identity, immutable URLs, graph closure and byte budgets before reading any asset. */
export function validateEngineAssetManifest(value, manifestUrl) {
    if (!value || value.schema !== 1 || value.format !== 'xrengine-assets'
        || !Array.isArray(value.assets) || value.assets.length < 1 || value.assets.length > CONTENT_LIMITS.assets)
        throw new Error('AssetSource.ManifestUnsupported: expected a bounded xrengine-assets manifest.');
    const assets = new Map();
    const foldedPaths = new Set();
    let total = 0;
    for (const entry of value.assets) {
        assetPath(entry?.path);
        if (foldedPaths.has(entry.path.toLowerCase()) || typeof entry.type !== 'string' || !entry.type || entry.type.length > 1024
            || !['cooked-binary', 'yaml', 'utf8-text'].includes(entry.encoding)
            || !Number.isSafeInteger(entry.bytes) || entry.bytes < 1 || entry.bytes > CONTENT_LIMITS.payloadBytes
            || !/^[a-f0-9]{64}$/.test(entry.hash) || entry.url !== `payload/${entry.hash}.bin`
            || !Array.isArray(entry.dependencies) || entry.dependencies.length > CONTENT_LIMITS.dependencies
            || new Set(entry.dependencies).size !== entry.dependencies.length)
            throw new Error(`AssetSource.InvalidEntry: '${entry.path}'.`);
        for (const dependency of entry.dependencies) assetPath(dependency);
        total += entry.bytes;
        if (total > CONTENT_LIMITS.selectedBytes)
            throw new Error('AssetSource.PackageBudgetExceeded: selected assets exceed the retained content budget.');
        foldedPaths.add(entry.path.toLowerCase());
        assets.set(entry.path, { ...entry, url: contentManifestUrl(entry.url, manifestUrl) });
    }
    assetPath(value.startupWorld);
    if (!assets.has(value.startupWorld)) throw new Error('AssetSource.StartupWorldMissing: startup world is not packaged.');
    if (value.startupSettings !== undefined && value.startupSettings !== null) {
        assetPath(value.startupSettings);
        if (!assets.has(value.startupSettings)) throw new Error('AssetSource.StartupSettingsMissing.');
    }
    if (value.shaderArtifacts !== undefined && value.shaderArtifacts !== null) {
        if (!Array.isArray(value.shaderArtifacts) || value.shaderArtifacts.length > 256)
            throw new Error('AssetSource.ShaderCatalogBudgetExceeded.');
        const identities = new Set();
        for (const shader of value.shaderArtifacts) {
            if (!shader || !/^[a-f0-9]{64}$/.test(shader.identity) || identities.has(shader.identity))
                throw new Error('AssetSource.ShaderIdentityInvalid.');
            assetPath(shader.descriptor); assetPath(shader.source);
            const descriptor = assets.get(shader.descriptor), source = assets.get(shader.source);
            if (!descriptor || !source || descriptor.encoding !== 'utf8-text' || source.encoding !== 'utf8-text'
                || descriptor.hash !== shader.identity)
                throw new Error('AssetSource.ShaderPayloadMissingOrMismatched.');
            identities.add(shader.identity);
        }
    }
    const heights = new Map();
    const active = new Set();
    function visit(path, depth) {
        if (active.has(path)) throw new Error(`AssetSource.DependencyCycle: '${path}'.`);
        if (heights.has(path)) {
            if (depth + heights.get(path) > CONTENT_LIMITS.depth) throw new Error('AssetSource.DependencyDepthExceeded.');
            return heights.get(path);
        }
        if (depth >= CONTENT_LIMITS.depth) throw new Error('AssetSource.DependencyDepthExceeded.');
        const asset = assets.get(path);
        if (!asset) throw new Error(`AssetSource.DependencyMissing: '${path}'.`);
        active.add(path);
        let height = 1;
        for (const dependency of asset.dependencies) height = Math.max(height, 1 + visit(dependency, depth + 1));
        active.delete(path);
        if (depth + height > CONTENT_LIMITS.depth) throw new Error('AssetSource.DependencyDepthExceeded.');
        heights.set(path, height);
        return height;
    }
    for (const path of assets.keys()) visit(path, 0);
    return { manifest: value, assets };
}

/** Imports retain owned bytes only; no managed memory view crosses an await. */
export const engineAssetImports = {
    create(url) {
        if (nextSource === 0x7fffffff) throw new Error('AssetSource.SessionCapacityExceeded.');
        const id = ++nextSource;
        sources.set(id, { loader: new BrowserContentLoader(contentManifestUrl(url)), assets: null,
            manifest: null, reads: new Map(), nextRead: 0, queue: [], active: 0, disposed: false });
        return id;
    },
    async open(id) {
        const source = requireSource(id);
        const manifest = await source.loader.readManifest();
        source.loader.signal.throwIfAborted();
        const validated = validateEngineAssetManifest(manifest, source.loader.manifestUrl);
        if (source !== requireSource(id)) throw new Error('AssetSource.StaleSession.');
        source.manifest = validated.manifest;
        source.assets = validated.assets;
    },
    manifest(id) {
        const source = requireSource(id);
        if (!source.manifest) throw new Error('AssetSource.NotReady.');
        return JSON.stringify(source.manifest);
    },
    beginRead(id, path) {
        const source = requireSource(id);
        const entry = source.assets?.get(path);
        if (!entry) throw new Error(`AssetSource.NotPackaged: '${path}'.`);
        if (source.reads.size >= CONTENT_LIMITS.assets || source.nextRead === 0x7fffffff)
            throw new Error('AssetSource.ReadCapacityExceeded.');
        const ticket = ++source.nextRead;
        const request = { bytes: null, released: false, settled: false, reject: null, resolve: null, controller: new AbortController() };
        request.promise = new Promise((resolve, reject) => { request.resolve = resolve; request.reject = reject; });
        // Install a rejection observer before .NET receives the promise.
        request.promise.catch(() => {});
        source.reads.set(ticket, request);
        source.queue.push({ request, entry });
        pump(source);
        return ticket;
    },
    async waitRead(id, ticket) {
        const source = requireSource(id);
        const request = source.reads.get(ticket);
        if (!request) throw new Error('AssetSource.StaleRead.');
        return await request.promise;
    },
    copyRead(id, ticket, destination) {
        const request = requireSource(id).reads.get(ticket);
        if (!request?.bytes || request.released) throw new Error('AssetSource.ReadNotReady.');
        if (destination?.byteLength !== request.bytes.byteLength) throw new Error('AssetSource.CopyLengthMismatch.');
        destination.set(request.bytes);
    },
    releaseRead(id, ticket) {
        const source = sources.get(id);
        const request = source?.reads.get(ticket);
        if (!request) return;
        source.reads.delete(ticket);
        const queued = source.queue.findIndex(item => item.request === request);
        if (queued >= 0) source.queue.splice(queued, 1);
        releaseRequest(source, request);
    },
    dispose(id) {
        const source = sources.get(id);
        if (!source) return;
        sources.delete(id);
        source.disposed = true;
        source.loader.dispose();
        for (const request of source.reads.values()) releaseRequest(source, request);
        source.reads.clear();
        source.queue.length = 0;
    },
};

function releaseRequest(source, request) {
    request.released = true;
    request.controller.abort(new Error('AssetSource.ReadCancelled.'));
    if (request.bytes) { source.loader.releasePayload(request.bytes); request.bytes = null; }
    if (!request.settled) request.reject(new Error('AssetSource.ReadCancelled.'));
}

function pump(source) {
    while (!source.disposed && source.active < CONTENT_LIMITS.concurrency && source.queue.length) {
        const { request, entry } = source.queue.shift();
        if (request.released) continue;
        source.active++;
        void (async () => {
            let bytes;
            try {
                bytes = await source.loader.readVerifiedPayload(entry.url, entry.bytes, entry.hash, entry.path, request.controller.signal);
                await source.loader.waitForIntegrationFrame(request.controller.signal);
                if (request.released || source.disposed) throw new Error('AssetSource.StaleRead.');
                request.bytes = bytes;
                bytes = null;
                request.settled = true;
                request.resolve(request.bytes.byteLength);
            } catch (error) {
                if (bytes) source.loader.releasePayload(bytes);
                request.settled = true;
                request.reject(error);
            } finally {
                source.active--;
                pump(source);
            }
        })();
    }
}
