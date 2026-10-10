/** Marks delivery classes on the canonical catalog entries; no second dependency graph is retained. */
export function classifyEngineAssetRoots(manifest, assets) {
    const hasEssential = manifest.essentialRoots !== undefined;
    const hasStreamed = manifest.streamedRoots !== undefined;
    if (!hasEssential && !hasStreamed) {
        if (manifest.shaderDelivery !== undefined) throw new Error('AssetSource.ShaderDeliveryRootsRequired.');
        for (const entry of assets.values()) entry.essential = true;
        return;
    }
    if (!hasEssential || !hasStreamed || !Array.isArray(manifest.essentialRoots) || !Array.isArray(manifest.streamedRoots)
        || manifest.essentialRoots.length < 1 || manifest.essentialRoots.length + manifest.streamedRoots.length > assets.size)
        throw new Error('AssetSource.DeliveryRootsInvalid.');
    const roots = new Set();
    for (const path of [...manifest.essentialRoots, ...manifest.streamedRoots]) {
        if (typeof path !== 'string' || !assets.has(path) || roots.has(path))
            throw new Error('AssetSource.DeliveryRootMissingOrRepeated.');
        roots.add(path);
    }
    const essential = new Set();
    function visit(path, closure) {
        if (closure.has(path)) return;
        closure.add(path);
        for (const dependency of assets.get(path).dependencies) visit(dependency, closure);
    }
    for (const path of manifest.essentialRoots) visit(path, essential);
    const required = [manifest.startupWorld, manifest.startupSettings, manifest.publishedMetadata, manifest.defaultUiFont];
    const deferred = validateShaderDelivery(manifest, assets, essential);
    for (const shader of manifest.shaderArtifacts ?? [])
        if (!deferred.has(shader.identity)) required.push(shader.descriptor, shader.source);
    for (const path of required)
        if (path != null && !essential.has(path)) throw new Error('AssetSource.StartupRootNotEssential.');
    const covered = new Set(essential);
    for (const path of manifest.streamedRoots) {
        // Closed-world managed type resolution checks XRScene assignability at use.
        // A JavaScript type-name comparison cannot recognize registered subclasses.
        if (essential.has(path) || !path.endsWith('.asset') || assets.get(path).encoding !== 'cooked-binary')
            throw new Error('AssetSource.StreamedRootInvalid: expected a deferred cooked asset outside the essential closure.');
        visit(path, covered);
    }
    for (const shader of manifest.shaderArtifacts ?? [])
        if (deferred.has(shader.identity)) {
            // Shader membership is delivery metadata, not a hydrated TextFile dependency.
            if (assets.get(shader.descriptor).dependencies.length || assets.get(shader.source).dependencies.length)
                throw new Error('AssetSource.ShaderDeliveryPayloadInvalid.');
            covered.add(shader.descriptor);
            covered.add(shader.source);
        }
    if (covered.size !== assets.size) throw new Error('AssetSource.DeliveryRootsIncomplete.');
    for (const entry of assets.values()) entry.essential = essential.has(entry.path);
}

/** Absence preserves the legacy all-essential contract; additions must cover the closed-world shader set exactly. */
function validateShaderDelivery(manifest, assets, essential) {
    if (manifest.shaderDelivery === undefined) return new Set();
    const delivery = manifest.shaderDelivery;
    if (!delivery || typeof delivery !== 'object' || Array.isArray(delivery)
        || Object.keys(delivery).length !== 3 || delivery.schema !== 1
        || !Array.isArray(delivery.scenes) || delivery.scenes.length > 256 || delivery.scenes.length !== manifest.streamedRoots.length)
        throw new Error('AssetSource.ShaderDeliveryInvalid.');
    const identities = new Set((manifest.shaderArtifacts ?? []).map(shader => shader.identity));
    function readIdentities(values) {
        if (!Array.isArray(values) || values.length > identities.size
            || new Set(values).size !== values.length || values.some(identity => !identities.has(identity)))
            throw new Error('AssetSource.ShaderDeliveryIdentityMissingOrRepeated.');
        return new Set(values);
    }
    const startup = readIdentities(delivery.startup), covered = new Set(startup), roots = new Set();
    for (const binding of [...(manifest.materialVariants ?? []), ...(manifest.pipelineArtifacts ?? []), ...(manifest.computeArtifacts ?? [])])
        if (!startup.has(binding.descriptorIdentity)) throw new Error('AssetSource.GlobalShaderBindingNotEssential.');
    for (const scene of delivery.scenes) {
        if (!scene || typeof scene !== 'object' || Array.isArray(scene) || Object.keys(scene).length !== 2
            || !manifest.streamedRoots.includes(scene.path) || roots.has(scene.path))
            throw new Error('AssetSource.SceneShaderDeliveryInvalid.');
        roots.add(scene.path);
        for (const identity of readIdentities(scene.identities)) covered.add(identity);
    }
    if (covered.size !== identities.size) throw new Error('AssetSource.ShaderDeliveryIncomplete.');
    const deferred = new Set();
    for (const shader of manifest.shaderArtifacts ?? []) {
        const required = startup.has(shader.identity);
        if (required !== essential.has(shader.descriptor) || required !== essential.has(shader.source))
            throw new Error('AssetSource.ShaderDeliveryEssentialMismatch.');
        if (assets.get(shader.descriptor).dependencies.length || assets.get(shader.source).dependencies.length)
            throw new Error('AssetSource.ShaderDeliveryPayloadInvalid.');
        if (!required) deferred.add(shader.identity);
    }
    return deferred;
}
