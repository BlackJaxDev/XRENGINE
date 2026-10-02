import { BrowserContentLoader } from './content-loader.js';
import { CONTENT_LIMITS, contentManifestUrl } from './content-manifest.js';
import { readSharedWorldPackage } from './engine-world-package.js';
import { classifyEngineAssetRoots } from './engine-asset-roots.js';
import { queueIntegration, finishIntegration, deliverySnapshot, INTEGRATION_LIMITS } from './engine-asset-delivery.js';

const sources = new Map();
let nextSource = 0;
const validSha256 = value => typeof value === 'string' && /^[a-f0-9]{64}(?![\s\S])/.test(value);
const pipelinePasses = new Set(['tonemap', 'depth-normal', 'gtao-generate', 'gtao-blur-horizontal',
    'gtao-blur-vertical', 'bloom-copy', 'bloom-downsample', 'bloom-upsample', 'bloom-combine']);

function requireSource(id) {
    const source = sources.get(id);
    if (!source) throw new Error('AssetSource.StaleSession: the content owner has been disposed.');
    return source;
}

function assetPath(path) {
    if (typeof path !== 'string' || path.length > 1024
        || !/^\/(engine|game)\/[A-Za-z0-9_. /-]+(?![\s\S])/.test(path)
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
            || !validSha256(entry.hash) || entry.url !== `payload/${entry.hash}.bin`
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
    if (value.worldPackage !== undefined && value.worldPackage !== 'world-package.json')
        throw new Error('AssetSource.WorldPackageInvalid: unsupported package descriptor location.');
    if (!assets.has(value.startupWorld)) throw new Error('AssetSource.StartupWorldMissing: startup world is not packaged.');
    if (value.startupSettings !== undefined && value.startupSettings !== null) {
        assetPath(value.startupSettings);
        if (!assets.has(value.startupSettings)) throw new Error('AssetSource.StartupSettingsMissing.');
    }
    if (value.publishedMetadata !== undefined) {
        assetPath(value.publishedMetadata);
        const metadata = assets.get(value.publishedMetadata);
        if (value.publishedMetadata !== '/engine/Metadata/AotRuntimeMetadata.bin' || !metadata
            || metadata.encoding !== 'cooked-binary'
            || !(metadata.type === 'XREngine.AotRuntimeMetadata, XREngine.Data'
                || metadata.type.startsWith('XREngine.AotRuntimeMetadata, XREngine.Data,'))
            || metadata.dependencies.length !== 0)
            throw new Error('AssetSource.PublishedMetadataInvalid: expected a standalone published type payload.');
    }
    if (value.defaultUiFont !== undefined) {
        assetPath(value.defaultUiFont);
        const font = assets.get(value.defaultUiFont);
        if (!value.defaultUiFont.startsWith('/engine/Fonts/') || !font
            || font.encoding !== 'cooked-binary'
            || !font.type.startsWith('XREngine.Rendering.FontGlyphSet, XREngine.Runtime.Rendering,')
            || font.dependencies.length !== 0)
            throw new Error('AssetSource.DefaultUiFontInvalid: expected a standalone cooked engine FontGlyphSet.');
    }
    const shaderIdentities = new Set();
    const shaderDescriptors = new Map();
    if (value.shaderArtifacts !== undefined && value.shaderArtifacts !== null) {
        if (!Array.isArray(value.shaderArtifacts) || value.shaderArtifacts.length > 256)
            throw new Error('AssetSource.ShaderCatalogBudgetExceeded.');
        for (const shader of value.shaderArtifacts) {
            if (!shader || !validSha256(shader.identity) || shaderIdentities.has(shader.identity))
                throw new Error('AssetSource.ShaderIdentityInvalid.');
            assetPath(shader.descriptor); assetPath(shader.source);
            const descriptor = assets.get(shader.descriptor), source = assets.get(shader.source);
            if (!descriptor || !source || descriptor.encoding !== 'utf8-text' || source.encoding !== 'utf8-text'
                || descriptor.hash !== shader.identity)
                throw new Error('AssetSource.ShaderPayloadMissingOrMismatched.');
            shaderIdentities.add(shader.identity);
            shaderDescriptors.set(shader.identity, descriptor);
        }
    }
    if (value.materialVariants !== undefined) {
        if (!Array.isArray(value.materialVariants) || value.materialVariants.length > 256)
            throw new Error('AssetSource.MaterialVariantBudgetExceeded.');
        const keys = new Set();
        const profile = /^[a-z][a-z0-9.-]{0,63}(?![\s\S])/;
        const validProfile = value => typeof value === 'string' && profile.test(value);
        for (const variant of value.materialVariants) {
            if (!variant || Object.keys(variant).length !== 7
                || !['StandardLitColor', 'StandardLitTexture', 'OpaqueShadowDepth', 'OpaquePointShadowDepth', 'OpaqueSpotShadowDepth', 'DebugPoint', 'DebugLine', 'DebugTriangle',
                    'UIQuadBatched', 'UIQuadBatchedTexture', 'UITextBatchedBitmap', 'SkyboxGradient', 'SkyboxEquirectangular', 'SkyboxOctahedral',
                    'SkyboxCubemap', 'SkyboxDynamicProcedural'].includes(variant.semantic)
                || !(variant.semanticVersion === 1 || variant.semantic === 'StandardLitColor' && variant.semanticVersion === 2)
                || variant.target !== 'WebGPUWgsl' || !validProfile(variant.pass)
                || !validProfile(variant.vertexProfile) || !validProfile(variant.outputProfile)
                || !shaderIdentities.has(variant.descriptorIdentity))
                throw new Error('AssetSource.MaterialVariantInvalid.');
            if (variant.semantic === 'StandardLitColor' && variant.semanticVersion === 2
                && !(variant.pass === 'forward-coverage' && variant.vertexProfile === 'static-position-normal-v1'
                    && ['linear-hdr-v1', 'linear-hdr-directional-shadow-v1', 'linear-hdr-local-shadows-v1'].includes(variant.outputProfile)
                    || variant.pass === 'depth-normal' && variant.vertexProfile === 'static-position-normal-v1'
                    && variant.outputProfile === 'normal-rgba16f-v1'
                    || variant.pass === 'depth' && variant.vertexProfile === 'static-position-v1'
                    && variant.outputProfile === 'depth-normal-v1'
                    || variant.pass === 'point-shadow-depth' && variant.vertexProfile === 'static-position-v1'
                    && variant.outputProfile === 'radial-r16f-v1'
                    || variant.pass === 'spot-shadow-depth' && variant.vertexProfile === 'static-position-v1'
                    && variant.outputProfile === 'projected-r16f-v1'))
                throw new Error('AssetSource.MaterialVariantInvalid.');
            if (variant.semantic === 'StandardLitTexture'
                && !(['position-normal-uv-v1', 'position-normal-tangent-uv-v1'].includes(variant.vertexProfile)
                    && (variant.pass === 'opaque-forward'
                        && ['linear-hdr-v1', 'linear-hdr-directional-shadow-v1', 'linear-hdr-local-shadows-v1'].includes(variant.outputProfile)
                        || variant.pass === 'depth-normal' && variant.vertexProfile === 'position-normal-tangent-uv-v1'
                        && variant.outputProfile === 'normal-rgba16f-v1')))
                throw new Error('AssetSource.MaterialVariantInvalid.');
            if (variant.semantic === 'OpaqueShadowDepth' && (variant.pass !== 'depth'
                || variant.vertexProfile !== 'static-position-v1' || variant.outputProfile !== 'depth-normal-v1'))
                throw new Error('AssetSource.MaterialVariantInvalid.');
            if (variant.semantic === 'OpaquePointShadowDepth' && (variant.pass !== 'point-shadow-depth'
                || variant.vertexProfile !== 'static-position-v1' || variant.outputProfile !== 'radial-r16f-v1'))
                throw new Error('AssetSource.MaterialVariantInvalid.');
            if (variant.semantic === 'OpaqueSpotShadowDepth' && (variant.pass !== 'spot-shadow-depth'
                || variant.vertexProfile !== 'static-position-v1' || variant.outputProfile !== 'projected-r16f-v1'))
                throw new Error('AssetSource.MaterialVariantInvalid.');
            if (['SkyboxGradient', 'SkyboxEquirectangular', 'SkyboxOctahedral', 'SkyboxCubemap', 'SkyboxDynamicProcedural'].includes(variant.semantic)
                && (variant.pass !== 'background' || variant.vertexProfile !== 'fullscreen-sky-v1'
                    || variant.outputProfile !== 'linear-hdr-v1'))
                throw new Error('AssetSource.MaterialVariantInvalid.');
            const debugProfiles = { DebugPoint: 'instanced-debug-point-v1', DebugLine: 'instanced-debug-line-v1', DebugTriangle: 'instanced-debug-triangle-v1' };
            if (debugProfiles[variant.semantic] && (variant.pass !== 'debug-overlay'
                || variant.vertexProfile !== debugProfiles[variant.semantic] || variant.outputProfile !== 'display-rgba-v1'))
                throw new Error('AssetSource.MaterialVariantInvalid.');
            const uiProfiles = { UIQuadBatched: 'instanced-ui-quad-v1', UIQuadBatchedTexture: 'instanced-ui-quad-texture-v1', UITextBatchedBitmap: 'instanced-ui-bitmap-text-v1' };
            if (uiProfiles[variant.semantic] && (variant.pass !== 'screen-ui'
                || variant.vertexProfile !== uiProfiles[variant.semantic] || variant.outputProfile !== 'display-rgba-v1'))
                throw new Error('AssetSource.MaterialVariantInvalid.');
            const key = [variant.semantic, variant.semanticVersion, variant.target, variant.pass,
                variant.vertexProfile, variant.outputProfile].join('\u001f');
            if (keys.has(key)) throw new Error('AssetSource.MaterialVariantDuplicateKey.');
            keys.add(key);
        }
    }
    if (value.pipelineArtifacts !== undefined) {
        if (!Array.isArray(value.pipelineArtifacts) || value.pipelineArtifacts.length > 16)
            throw new Error('AssetSource.PipelineArtifactBudgetExceeded.');
        const passes = new Set();
        for (const pipeline of value.pipelineArtifacts) {
            if (!pipeline || typeof pipeline !== 'object' || Array.isArray(pipeline)
                || Object.keys(pipeline).length !== 2 || !pipelinePasses.has(pipeline.pass)
                || !validSha256(pipeline.descriptorIdentity))
                throw new Error('AssetSource.PipelineArtifactInvalid.');
            const descriptor = shaderDescriptors.get(pipeline.descriptorIdentity);
            if (!descriptor) throw new Error('AssetSource.PipelineArtifactMissing.');
            if (descriptor.bytes > CONTENT_LIMITS.jsonBytes)
                throw new Error('AssetSource.PipelineArtifactDescriptorBudgetExceeded.');
            if (passes.has(pipeline.pass)) throw new Error('AssetSource.PipelineArtifactDuplicatePass.');
            passes.add(pipeline.pass);
        }
    }
    if (value.computeArtifacts !== undefined) {
        if (!Array.isArray(value.computeArtifacts) || value.computeArtifacts.length > 4)
            throw new Error('AssetSource.ComputeArtifactBudgetExceeded.');
        const kernels = new Set();
        for (const compute of value.computeArtifacts) {
            if (!compute || typeof compute !== 'object' || Array.isArray(compute)
                || Object.keys(compute).length !== 2
                || !['packed-skinning', 'luminance-reduction', 'luminance-reduction-2d', 'luminance-mipmap'].includes(compute.kernel)
                || !validSha256(compute.descriptorIdentity) || kernels.has(compute.kernel))
                throw new Error('AssetSource.ComputeArtifactInvalid.');
            kernels.add(compute.kernel);
            const descriptor = shaderDescriptors.get(compute.descriptorIdentity);
            if (!descriptor) throw new Error('AssetSource.ComputeArtifactMissing.');
            if (descriptor.bytes > CONTENT_LIMITS.jsonBytes)
                throw new Error('AssetSource.ComputeArtifactDescriptorBudgetExceeded.');
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
    classifyEngineAssetRoots(value, assets);
    return { manifest: value, assets };
}

async function validatePipelineArtifactDescriptors(loader, { manifest, assets }) {
    for (const pipeline of manifest.pipelineArtifacts ?? []) {
        const shader = manifest.shaderArtifacts.find(artifact => artifact.identity === pipeline.descriptorIdentity);
        const entry = assets.get(shader.descriptor);
        const bytes = await loader.readVerifiedPayload(entry.url, entry.bytes, pipeline.descriptorIdentity, entry.path);
        try {
            const descriptor = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
            if (!descriptor || descriptor.pass !== pipeline.pass || descriptor.target !== 'WebGPUWgsl'
                || !descriptor.entryPoints || typeof descriptor.entryPoints !== 'object'
                || Object.keys(descriptor.entryPoints).length !== 2
                || typeof descriptor.entryPoints.vertex !== 'string' || !descriptor.entryPoints.vertex
                || typeof descriptor.entryPoints.fragment !== 'string' || !descriptor.entryPoints.fragment
                || Object.hasOwn(descriptor, 'materialVariant'))
                throw new Error('AssetSource.PipelineArtifactDescriptorMismatch.');
        } finally {
            loader.releasePayload(bytes);
        }
    }
    for (const compute of manifest.computeArtifacts ?? []) {
        const shader = manifest.shaderArtifacts.find(artifact => artifact.identity === compute.descriptorIdentity);
        const entry = assets.get(shader.descriptor);
        const bytes = await loader.readVerifiedPayload(entry.url, entry.bytes, compute.descriptorIdentity, entry.path);
        try {
            const descriptor = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
            if (compute.kernel === 'luminance-reduction' || compute.kernel === 'luminance-reduction-2d') {
                const names = ['Source', 'Partials', 'Result', 'Parameters'];
                const physical = ['source', 'partials', 'result', 'parameters'];
                const kinds = [compute.kernel === 'luminance-reduction-2d' ? 'texture-2d-float' : 'texture-2d-array-float',
                    'storage', 'storage', 'uniform'];
                const sizes = [0, 8, 4, 64];
                const bindings = descriptor?.layout?.bindings;
                const members = bindings?.[3]?.members;
                const memberNames = ['origin', 'extent', 'mip', 'layers', 'tilesX', 'tilesY', 'tileCount', 'mode', 'weights'];
                const providers = ['Origin', 'Extent', 'Mip', 'Layers', 'TilesX', 'TilesY', 'TileCount', 'Mode', 'Weights'];
                const types = ['vec2<u32>', 'vec2<u32>', 'u32', 'u32', 'u32', 'u32', 'u32', 'u32', 'vec4<f32>'];
                const offsets = [0, 8, 16, 20, 24, 28, 32, 36, 48];
                const bytesPerMember = [8, 8, 4, 4, 4, 4, 4, 4, 16];
                if (descriptor?.pass !== compute.kernel || descriptor.target !== 'WebGPUWgsl'
                    || descriptor.semanticSchemaIdentity !== 'xrengine.engine.compute.v1'
                    || !descriptor.entryPoints || Object.keys(descriptor.entryPoints).length !== 1
                    || descriptor.entryPoints.compute !== 'reduce'
                    || !Array.isArray(descriptor.workgroupSize)
                    || descriptor.workgroupSize.length !== 3 || descriptor.workgroupSize.some((size, i) => size !== [256, 1, 1][i])
                    || Object.hasOwn(descriptor, 'materialVariant')
                    || !descriptor.pipeline || typeof descriptor.pipeline !== 'object'
                    || Array.isArray(descriptor.pipeline) || Object.keys(descriptor.pipeline).length !== 0
                    || !Array.isArray(bindings) || bindings.length !== 4
                    || bindings.some((binding, i) => binding.name !== names[i] || binding.physicalName !== physical[i]
                        || binding.kind !== kinds[i] || binding.group !== 0 || binding.binding !== i
                        || binding.bytes !== sizes[i] || binding.owner !== 'Engine' || binding.frequency !== 'Object'
                        || binding.dynamic !== false || (binding.runtimeArray === true) !== (i === 1 || i === 2)
                        || !Array.isArray(binding.visibility) || binding.visibility.length !== 1 || binding.visibility[0] !== 'compute'
                        || !Array.isArray(binding.members) || binding.members.length !== (i === 3 ? 9 : 0))
                    || members.some((member, i) => member.name !== memberNames[i] || member.provider !== providers[i]
                        || member.type !== types[i] || member.offset !== offsets[i] || member.bytes !== bytesPerMember[i]))
                    throw new Error('AssetSource.ComputeArtifactDescriptorMismatch.');
                continue;
            }
            if (compute.kernel === 'luminance-mipmap') {
                const names = ['Source', 'Output', 'Parameters'];
                const physical = ['source', 'output', 'parameters'];
                const kinds = ['texture-2d-float', 'storage', 'uniform'];
                const sizes = [0, 4, 32];
                const bindings = descriptor?.layout?.bindings;
                const members = bindings?.[2]?.members;
                const memberNames = ['srcMip', 'dstWidth', 'dstHeight', 'srcWidth', 'srcHeight', 'rowWords', 'encoding', 'mode'];
                const providers = ['SrcMip', 'DstWidth', 'DstHeight', 'SrcWidth', 'SrcHeight', 'RowWords', 'Encoding', 'Mode'];
                if (descriptor?.pass !== 'luminance-mipmap' || descriptor.target !== 'WebGPUWgsl'
                    || descriptor.semanticSchemaIdentity !== 'xrengine.engine.compute.v1'
                    || !descriptor.entryPoints || Object.keys(descriptor.entryPoints).length !== 1
                    || descriptor.entryPoints.compute !== 'generate'
                    || !Array.isArray(descriptor.workgroupSize)
                    || descriptor.workgroupSize.length !== 3 || descriptor.workgroupSize.some((size, i) => size !== [16, 16, 1][i])
                    || Object.hasOwn(descriptor, 'materialVariant')
                    || !descriptor.pipeline || typeof descriptor.pipeline !== 'object'
                    || Array.isArray(descriptor.pipeline) || Object.keys(descriptor.pipeline).length !== 0
                    || !Array.isArray(bindings) || bindings.length !== 3
                    || bindings.some((binding, i) => binding.name !== names[i] || binding.physicalName !== physical[i]
                        || binding.kind !== kinds[i] || binding.group !== 0 || binding.binding !== i
                        || binding.bytes !== sizes[i] || binding.owner !== 'Engine' || binding.frequency !== 'Object'
                        || binding.dynamic !== false || (binding.runtimeArray === true) !== (i === 1)
                        || !Array.isArray(binding.visibility) || binding.visibility.length !== 1 || binding.visibility[0] !== 'compute'
                        || !Array.isArray(binding.members) || binding.members.length !== (i === 2 ? 8 : 0))
                    || members.some((member, i) => member.name !== memberNames[i] || member.provider !== providers[i]
                        || member.type !== 'u32' || member.offset !== i * 4 || member.bytes !== 4))
                    throw new Error('AssetSource.ComputeArtifactDescriptorMismatch.');
                continue;
            }
            const expectedNames = ['PackedSkinningData', 'BonePalette', 'ActiveMorphs', 'DeformedPositions', 'DeformedAttributes', 'Update'];
            const physicalNames = ['data', 'palette', 'activeMorphs', 'vertices', 'attributes', 'update'];
            const expectedKinds = ['read-only-storage', 'read-only-storage', 'read-only-storage', 'storage', 'storage', 'uniform'];
            const expectedBytes = [4, 16, 8, 4, 16, 16];
            const bindings = descriptor?.layout?.bindings;
            if (descriptor?.pass !== 'skinning' || descriptor.target !== 'WebGPUWgsl'
                || descriptor.semanticSchemaIdentity !== 'xrengine.engine.compute.v1'
                || !descriptor.entryPoints || Object.keys(descriptor.entryPoints).length !== 1
                || descriptor.entryPoints.compute !== 'skin'
                || !Array.isArray(descriptor.workgroupSize)
                || descriptor.workgroupSize.length !== 3 || descriptor.workgroupSize.some((size, i) => size !== [64, 1, 1][i])
                || Object.hasOwn(descriptor, 'materialVariant')
                || !descriptor.pipeline || typeof descriptor.pipeline !== 'object'
                || Array.isArray(descriptor.pipeline) || Object.keys(descriptor.pipeline).length !== 0
                || !Array.isArray(bindings) || bindings.length !== 6
                || bindings.some((binding, i) => binding.name !== expectedNames[i] || binding.physicalName !== physicalNames[i]
                    || binding.kind !== expectedKinds[i]
                    || binding.group !== 0 || binding.binding !== i || binding.bytes !== expectedBytes[i]
                    || binding.owner !== 'Engine' || binding.frequency !== 'Object'
                    || binding.dynamic !== (i === 5) || (binding.runtimeArray === true) !== (i !== 5)
                    || !Array.isArray(binding.visibility) || binding.visibility.length !== 1 || binding.visibility[0] !== 'compute'
                    || !Array.isArray(binding.members) || binding.members.length !== (i === 5 ? 4 : 0))
                || bindings[5].members.some((member, i) => member.name !== ['activeCount', 'reserved0', 'reserved1', 'reserved2'][i]
                    || member.provider !== ['ActiveMorphCount', 'Reserved0', 'Reserved1', 'Reserved2'][i]
                    || member.offset !== i * 4 || member.bytes !== 4 || member.type !== 'u32'))
                throw new Error('AssetSource.ComputeArtifactDescriptorMismatch.');
        } finally {
            loader.releasePayload(bytes);
        }
    }
}

/** Imports retain owned bytes only; no managed memory view crosses an await. */
export const engineAssetImports = {
    create(url) {
        if (nextSource === 0x7fffffff) throw new Error('AssetSource.SessionCapacityExceeded.');
        const id = ++nextSource;
        sources.set(id, { loader: new BrowserContentLoader(contentManifestUrl(url)), assets: null,
            manifest: null, worldPackage: null, reads: new Map(), nextRead: 0, queue: [], active: 0, disposed: false,
            integrations: new Map(), nextIntegration: 0, deliveryState: 'opening', integrationsCompleted: 0,
            integrationMilliseconds: 0, longestIntegrationMilliseconds: 0, overBudgetIntegrations: 0,
            preflightReceivedBytes: 0, descriptorReceivedBytes: 0, descriptorRetries: 0, descriptorPeakStagingBytes: 0, descriptorLoader: null,
            failedReads: 0, cancelledReads: 0, retainedNativeEstimateBytes: 0, heldManagedBytes: 0, lastError: null });
        globalThis.dispatchEvent?.(new Event('xrengine-assets-active'));
        return id;
    },
    async open(id) {
        const source = requireSource(id);
        const initialManifest = await source.loader.readManifest();
        const { manifest, worldPackage, descriptorStatistics } = await readSharedWorldPackage(source.loader, initialManifest,
            loader => { if (loader) source.descriptorLoader = loader; });
        source.descriptorReceivedBytes = descriptorStatistics?.receivedDecodedBytes ?? 0;
        source.descriptorRetries = descriptorStatistics?.retries ?? 0;
        source.descriptorPeakStagingBytes = descriptorStatistics?.peakStagingBytes ?? 0;
        source.descriptorLoader = null;
        source.loader.signal.throwIfAborted();
        const validated = validateEngineAssetManifest(manifest, source.loader.manifestUrl);
        await validatePipelineArtifactDescriptors(source.loader, validated);
        source.preflightReceivedBytes = source.loader.getStatistics().receivedDecodedBytes + source.descriptorReceivedBytes;
        source.loader.signal.throwIfAborted();
        if (source !== requireSource(id)) throw new Error('AssetSource.StaleSession.');
        source.manifest = validated.manifest;
        source.assets = validated.assets;
        source.worldPackage = worldPackage;
        source.selectedBytes = source.essentialBytes = source.essentialAssets = 0;
        for (const entry of source.assets.values()) {
            source.selectedBytes += entry.bytes;
            if (entry.essential) { source.essentialBytes += entry.bytes; source.essentialAssets++; }
        }
        source.deliveryState = 'ready';
    },
    progress(id) { return JSON.stringify(deliverySnapshot(requireSource(id))); },
    currentProgress() {
        let latest = null;
        for (const source of sources.values()) latest = source;
        return JSON.stringify(latest ? deliverySnapshot(latest) : null);
    },
    getProgress() { return Array.from(sources, ([id, source]) => ({ source: id, ...deliverySnapshot(source) })); },
    adjustManagedStaging(id, bytes) {
        const source = sources.get(id);
        if (!source) return;
        const total = source.heldManagedBytes + bytes;
        if (!Number.isSafeInteger(bytes) || total < 0 || total > CONTENT_LIMITS.stagingBytes)
            throw new Error('AssetSource.ManagedStagingBudgetExceeded.');
        source.heldManagedBytes = total;
    },
    beginIntegration(id, path, byteLength, companionPath = '') {
        const source = requireSource(id);
        const entry = source.assets?.get(path);
        const companion = companionPath ? source.assets?.get(companionPath) : null;
        if (!entry || entry.bytes !== byteLength || companionPath && !companion || source.nextIntegration === 0x7fffffff)
            throw new Error('AssetSource.IntegrationInvalid.');
        const ticket = ++source.nextIntegration;
        const item = queueIntegration(source, byteLength + (companion?.bytes ?? 0));
        item.managedCompanionBytes = companion?.bytes ?? 0;
        source.integrations.set(ticket, item);
        globalThis.dispatchEvent?.(new Event('xrengine-assets-active'));
        return ticket;
    },
    async waitIntegration(id, ticket) {
        const item = requireSource(id).integrations.get(ticket);
        if (!item) throw new Error('AssetSource.StaleIntegration.');
        await item.promise;
    },
    finishIntegration(id, ticket) {
        const source = sources.get(id), item = source?.integrations.get(ticket);
        if (!item) return;
        source.integrations.delete(ticket);
        const elapsed = finishIntegration(item);
        if (item.started !== null) {
            source.integrationsCompleted++;
            source.integrationMilliseconds += elapsed;
            source.longestIntegrationMilliseconds = Math.max(source.longestIntegrationMilliseconds, elapsed);
            if (elapsed > INTEGRATION_LIMITS.millisecondsPerFrame) source.overBudgetIntegrations++;
        }
    },
    retain(id, path, serializedBytes, objects, managedBytes, nativeBytes) {
        const source = requireSource(id), entry = source.assets?.get(path);
        if (!entry || entry.bytes !== serializedBytes || ![objects, managedBytes, nativeBytes].every(value => Number.isSafeInteger(value) && value >= 0))
            throw new Error('AssetSource.RetentionInvalid.');
        entry.retained = { serializedBytes, objects, managedBytes };
        source.retainedNativeEstimateBytes = nativeBytes;
    },
    releaseAsset(id, path, nativeBytes) {
        const source = sources.get(id), entry = source?.assets?.get(path);
        if (entry) entry.retained = null;
        if (source) source.retainedNativeEstimateBytes = nativeBytes;
    },
    manifest(id) {
        const source = requireSource(id);
        if (!source.manifest) throw new Error('AssetSource.NotReady.');
        return JSON.stringify(source.manifest);
    },
    verifiedWorldPackage(id) {
        const source = requireSource(id);
        if (!source.manifest) throw new Error('AssetSource.NotReady.');
        return source.worldPackage ? JSON.stringify(source.worldPackage) : '';
    },
    beginRead(id, path) {
        const source = requireSource(id);
        const entry = source.assets?.get(path);
        if (!entry) throw new Error(`AssetSource.NotPackaged: '${path}'.`);
        if (source.reads.size >= CONTENT_LIMITS.assets || source.nextRead === 0x7fffffff)
            throw new Error('AssetSource.ReadCapacityExceeded.');
        const ticket = ++source.nextRead;
        const request = { bytes: null, released: false, settled: false, active: false, reject: null, resolve: null, controller: new AbortController() };
        request.promise = new Promise((resolve, reject) => { request.resolve = resolve; request.reject = reject; });
        // Install a rejection observer before .NET receives the promise.
        request.promise.catch(() => {});
        source.reads.set(ticket, request);
        source.queue.push({ request, entry });
        globalThis.dispatchEvent?.(new Event('xrengine-assets-active'));
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
        for (const item of source.integrations.values()) finishIntegration(item);
        source.integrations.clear();
        for (const request of source.reads.values()) releaseRequest(source, request);
        source.reads.clear();
        source.queue.length = 0;
    },
};

function releaseRequest(source, request) {
    if (request.released) return;
    request.released = true;
    request.controller.abort(new Error('AssetSource.ReadCancelled.'));
    if (request.bytes) { source.loader.releasePayload(request.bytes); request.bytes = null; }
    if (!request.settled) { source.cancelledReads++; request.reject(new Error('AssetSource.ReadCancelled.')); }
    if (request.active && request.settled) { request.active = false; source.active--; pump(source); }
}

function pump(source) {
    while (!source.disposed && source.active < CONTENT_LIMITS.concurrency && source.queue.length) {
        const { request, entry } = source.queue.shift();
        if (request.released) continue;
        source.active++;
        request.active = true;
        void (async () => {
            let bytes;
            try {
                bytes = await source.loader.readVerifiedPayload(entry.url, entry.bytes, entry.hash, entry.path, request.controller.signal);
                if (request.released || source.disposed) throw new Error('AssetSource.StaleRead.');
                entry.verified = true;
                request.bytes = bytes;
                bytes = null;
                request.settled = true;
                request.resolve(request.bytes.byteLength);
            } catch (error) {
                if (bytes) source.loader.releasePayload(bytes);
                request.settled = true;
                if (!request.released && !source.disposed) { source.failedReads++; source.lastError = String(error.message ?? error); }
                request.reject(error);
            } finally {
                if (!request.bytes && request.active) { request.active = false; source.active--; }
                pump(source);
            }
        })();
    }
}
