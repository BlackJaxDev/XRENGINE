import { BrowserContentLoader } from './content-loader.js';
import { CONTENT_LIMITS, contentManifestUrl } from './content-manifest.js';

const hashPattern = /^[a-f0-9]{64}(?![\s\S])/;
const filePattern = /^[A-Za-z0-9_.\/-]+(?![\s\S])/;
const packageName = 'world-package.json';
const catalogName = 'manifest.json';

function fail(detail) { throw new Error(`AssetSource.WorldPackageInvalid: ${detail}`); }
function text(value, label) {
    if (typeof value !== 'string' || !value.trim() || value.length > 1024) fail(label);
    return value;
}
function relativePath(value) {
    if (typeof value !== 'string' || value.length > 512 || !filePattern.test(value)
        || value.split('/').some(part => !part || part === '.' || part === '..')) fail('unsafe file path');
    return value;
}
function hash(value) {
    if (typeof value !== 'string') fail('missing SHA-256 identity');
    const normalized = value.startsWith('sha256:') ? value.slice(7) : value;
    if (!hashPattern.test(normalized)) fail('invalid SHA-256 identity');
    return normalized;
}
function stringMap(value, label) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) fail(label);
    const keys = Object.keys(value);
    const folded = new Set();
    if (keys.length > 64) fail(`${label} exceeds its entry bound`);
    for (const key of keys) {
        text(key, label);
        if (typeof value[key] !== 'string' || value[key].length > 4096 || folded.has(key.toLowerCase())) fail(label);
        folded.add(key.toLowerCase());
    }
    return keys;
}

/** Matches WorldPackageManifestBuilder's length-prefixed UTF-8 canonical identity, including file order. */
export async function validateSharedWorldPackage(manifest) {
    if (!manifest || manifest.schemaVersion !== 1 || manifest.gameBootstrapId !== 'world-v1'
        || !Number.isSafeInteger(manifest.totalBytes) || manifest.totalBytes < 1
        || manifest.totalBytes > CONTENT_LIMITS.selectedBytes
        || !Array.isArray(manifest.files) || manifest.files.length < 3 || manifest.files.length > CONTENT_LIMITS.assets)
        fail('unsupported schema, bootstrap or file budget');
    text(manifest.packageId, 'package identity');
    text(manifest.buildVersion, 'build version');
    const asset = manifest.asset;
    if (!asset || !Number.isSafeInteger(asset.assetSchemaVersion) || asset.assetSchemaVersion < 1
        || asset.assetSchemaVersion > 0x7fffffff || asset.requiredBuildVersion !== manifest.buildVersion)
        fail('asset or build identity');
    text(asset.worldId, 'world identity');
    text(asset.revisionId, 'world revision');
    stringMap(asset.metadata, 'asset metadata');
    const metadataKeys = stringMap(manifest.metadata, 'package metadata');
    if (manifest.metadata.browserCatalog !== catalogName
        || typeof manifest.metadata.browserStartupWorld !== 'string') fail('missing browser catalog binding');
    const entryPoint = relativePath(manifest.worldEntryPoint);
    if (!entryPoint.endsWith('.asset') || ['payload/', 'manifest.json/', 'world-package.json/']
        .some(prefix => entryPoint.toLowerCase().startsWith(prefix))) fail('native world entry point');
    if (manifest.metadata.browserStartupWorld !== `/game/${entryPoint}`) fail('native and browser world paths differ');
    const files = new Map(), folded = new Set();
    let total = 0;
    for (const file of manifest.files) {
        relativePath(file?.relativePath);
        if (file.relativePath === packageName || folded.has(file.relativePath.toLowerCase())
            || !Number.isSafeInteger(file.length) || file.length < 1 || file.length > CONTENT_LIMITS.payloadBytes)
            fail('duplicate file or unsupported byte budget');
        hash(file.sha256);
        total += file.length;
        folded.add(file.relativePath.toLowerCase());
        files.set(file.relativePath, file);
    }
    if (total !== manifest.totalBytes || !files.has(entryPoint) || !files.has(catalogName)
        || files.get(catalogName).length > CONTENT_LIMITS.manifestBytes) fail('file set does not match package totals or entry points');
    // The bounded shared profile has one native world. Every other file is the
    // exact catalog or one of its immutable cooked payloads, never ambient roots.
    for (const path of files.keys())
        if (path !== entryPoint && path !== catalogName && !/^payload\/[a-f0-9]{64}\.bin(?![\s\S])/.test(path))
            fail('unsupported native dependencies');

    const encoder = new TextEncoder();
    const fields = [manifest.schemaVersion, manifest.packageId, asset.worldId, asset.revisionId,
        asset.assetSchemaVersion, asset.requiredBuildVersion, entryPoint, manifest.gameBootstrapId,
        manifest.buildVersion, manifest.totalBytes];
    // JavaScript's default ordering is ordinal UTF-16, matching StringComparer.Ordinal.
    for (const key of metadataKeys.sort()) fields.push(key, manifest.metadata[key]);
    for (const file of manifest.files) fields.push(file.relativePath, file.length, hash(file.sha256));
    const canonical = fields.map(value => {
        const valueText = String(value);
        return `${encoder.encode(valueText).byteLength}:${valueText}\n`;
    }).join('');
    if (!globalThis.crypto?.subtle) fail('SHA-256 requires a secure browser context');
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(canonical)));
    let actual = '';
    for (const byte of digest) actual += byte.toString(16).padStart(2, '0');
    if (actual !== hash(manifest.manifestHash) || actual !== hash(asset.contentHash)) fail('canonical manifest hash mismatch');
    return files;
}

/** Verifies the complete shared package before exposing either its catalog or native admission identity. */
export async function readSharedWorldPackage(loader, initialManifest) {
    if (initialManifest?.worldPackage === undefined) return { manifest: initialManifest, worldPackage: null };
    if (initialManifest.worldPackage !== packageName
        || new URL(loader.manifestUrl).pathname.split('/').at(-1) !== catalogName)
        fail('unsupported package descriptor location');
    const descriptorLoader = new BrowserContentLoader(contentManifestUrl(packageName, loader.manifestUrl), loader.signal);
    let worldPackage;
    try { worldPackage = await descriptorLoader.readManifest(); }
    finally { descriptorLoader.dispose(); }
    loader.signal.throwIfAborted();
    const files = await validateSharedWorldPackage(worldPackage);
    loader.signal.throwIfAborted();
    let manifest;
    for (const file of files.values()) {
        const bytes = await loader.readVerifiedPayload(contentManifestUrl(file.relativePath, loader.manifestUrl),
            file.length, hash(file.sha256), file.relativePath);
        try {
            if (file.relativePath === catalogName)
                manifest = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
        } finally { loader.releasePayload(bytes); }
    }
    loader.signal.throwIfAborted();
    if (manifest?.worldPackage !== packageName || manifest.startupWorld !== worldPackage.metadata.browserStartupWorld
        || !Array.isArray(manifest.assets)) fail('verified browser startup binding differs');
    const expectedFiles = new Set([catalogName, worldPackage.worldEntryPoint]);
    for (const entry of manifest.assets) {
        const file = files.get(entry?.url);
        if (!file || file.length !== entry.bytes || hash(file.sha256) !== entry.hash) fail('catalog payload is not bound by the native package');
        expectedFiles.add(entry.url);
    }
    if (expectedFiles.size !== files.size) fail('shared package contains unreferenced payloads');
    return { manifest, worldPackage };
}
