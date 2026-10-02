/** Marks delivery classes on the canonical catalog entries; no second dependency graph is retained. */
export function classifyEngineAssetRoots(manifest, assets) {
    const hasEssential = manifest.essentialRoots !== undefined;
    const hasStreamed = manifest.streamedRoots !== undefined;
    if (!hasEssential && !hasStreamed) {
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
    for (const shader of manifest.shaderArtifacts ?? []) required.push(shader.descriptor, shader.source);
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
    if (covered.size !== assets.size) throw new Error('AssetSource.DeliveryRootsIncomplete.');
    for (const entry of assets.values()) entry.essential = essential.has(entry.path);
}
