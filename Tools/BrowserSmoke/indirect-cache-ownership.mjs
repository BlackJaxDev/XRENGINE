const selectionLabel = 'Authored indexed selected mesh and LOD';
const argumentsLabel = 'Authored whole-primitive indexed arguments';
const selectProgram = 'engine-meshlets-select-lod';
const cullProgram = 'engine-indirect-cull-primitive';
function require(condition, detail) {
    if (!condition) throw new Error('BrowserSmoke.UnlitIndirectCacheOwnership: ' + detail);
}

/** Qualifies the exact static Unlit fixture's partial three-slot caches without changing renderer policy. */
export function inspectIndirectCacheOwnership(stage) {
    const state = stage.states[0], inventory = state.resourceOwnership, cache = state.indexedCache;
    const sources = stage.profile === 'gpu-indirect-x4' ? 12 : 10;
    require(inventory && cache && inventory.owner === cache.owner && inventory.owner === stage.pause.session &&
        inventory.generation === cache.outputGeneration && cache.frameSequence === state.submittedFrame.sequence &&
        cache.slotCapacity === 3 && cache.sourceCapacity === 256 && cache.drawCapacity === 256 &&
        cache.slots.length === 3, 'missing or mismatched accepted-frame owner map');
    const nodes = new Map(inventory.resources.map(node => [node.handle, node]));
    require(nodes.size === inventory.resources.length && nodes.size === stage.statistics.resources.live &&
        nodes.size === stage.postReadbackStatistics.resources.live,
        'the complete live resource table must match the sampled inventory');
    for (const node of nodes.values()) {
        require(Number.isSafeInteger(node.handle) && node.handle > 0 && !node.retired,
            'retired or invalid resource survived settled sampling');
        require(new Set(node.dependencies).size === node.dependencies.length &&
            node.dependencies.every(handle => nodes.has(handle)), 'dependency escaped the current owner table');
    }
    const variable = new Set(), bufferOwners = new Map(), pipelineOwners = new Map(), owners = [];
    const add = (handle, kind, owner, pending = false) => {
        require(Number.isSafeInteger(handle) && handle >= 0, 'invalid partial-owner handle');
        if (!handle) return;
        const node = nodes.get(handle);
        require(node?.kind === kind && !variable.has(handle), 'missing or multiply owned cache resource');
        variable.add(handle);
        if (kind === 'buffer') {
            const argumentsBuffer = owner.kind === 'arguments';
            require(node.label === (argumentsBuffer ? argumentsLabel : selectionLabel) &&
                node.size === (argumentsBuffer ? 32 : 16) && node.usage === (argumentsBuffer ? 392 : 136),
                'slot buffer does not match the exact indirect ABI');
            bufferOwners.set(handle, { ...owner, pending });
        } else {
            require(node.label.startsWith('engine-unlit-') && owner.outputGeneration === cache.outputGeneration,
                'retained raster pipeline belongs to an obsolete output or another program');
            pipelineOwners.set(handle, owner);
        }
    };
    for (let slotIndex = 0; slotIndex < cache.slots.length; slotIndex++) {
        const slot = cache.slots[slotIndex];
        require(slot.slot === slotIndex && slot.selections.length <= sources && slot.works.length <= sources,
            'the fixed fixture exceeded its per-slot source or draw bound');
        for (let index = 0; index < slot.selections.length; index++) {
            const selection = slot.selections[index], owner = { slot: slotIndex, index, kind: 'selection', key: `${slotIndex}/selection/${index}` };
            require(selection.index === index, 'selection ordinals are not exact');
            require(!selection.handle || !selection.pendingHandle, 'fixed-size selection retained two physical generations');
            owners.push(owner);
            add(selection.handle, 'buffer', owner);
            add(selection.pendingHandle, 'buffer', owner, true);
        }
        for (let index = 0; index < slot.works.length; index++) {
            const work = slot.works[index], owner = { ...work, slot: slotIndex, kind: 'arguments', key: `${slotIndex}/work/${index}` };
            require(work.index === index, 'work ordinals are not exact');
            require(!work.arguments || !work.pendingArguments, 'fixed-size arguments retained two physical generations');
            owners.push(owner);
            add(work.arguments, 'buffer', owner);
            add(work.pendingArguments, 'buffer', owner, true);
            add(work.pipeline, 'render-pipeline', owner);
        }
    }
    const groups = new Map(), fanout = new Map();
    const count = (owner, category) => {
        const key = `${owner.key}/${category}`, value = (fanout.get(key) ?? 0) + 1;
        fanout.set(key, value);
        require(value === 1, 'one fixture cache owner retained duplicate ' + category);
    };
    for (const node of nodes.values()) {
        if (node.kind !== 'binding-group') continue;
        const roots = node.dependencies.filter(handle => bufferOwners.has(handle)).map(handle => bufferOwners.get(handle));
        if (!roots.length) continue;
        const argumentsOwner = roots.find(owner => owner.kind === 'arguments');
        const selection = roots.filter(owner => owner.kind === 'selection');
        require(selection.length === 1 && roots.length === (argumentsOwner ? 2 : 1) &&
            roots.every(owner => !owner.pending && owner.slot === selection[0].slot && owner.index === selection[0].index),
            'compute group mixes invalid slot or source roots');
        const owner = argumentsOwner ?? selection[0], program = argumentsOwner ? cullProgram : selectProgram;
        const layouts = node.dependencies.filter(handle => nodes.get(handle).kind === 'binding-layout');
        require(layouts.length === 1 && [...nodes.values()].some(pipeline => pipeline.kind === 'compute-pipeline' &&
            pipeline.label === program && pipeline.dependencies.includes(layouts[0])), 'compute group has the wrong program layout');
        count(owner, 'binding group');
        variable.add(node.handle); groups.set(node.handle, { owner, program });
    }
    const targets = new Map(state.targets.map(target => [target.generation * 0x10000 + target.slot, target]));
    for (const node of nodes.values()) {
        if (node.kind !== 'commands') continue;
        for (const operation of node.operations) for (const attachment of operation.attachments) {
            if (attachment.view <= 0) continue;
            const view = nodes.get(attachment.view), target = targets.get(attachment.texture);
            require(view?.kind === 'texture-view' && view.dependencies.includes(attachment.texture) && target &&
                attachment.width === target.width && attachment.height === target.height &&
                target.width === stage.width && target.height === stage.height,
                'retained command references a prior output generation or an unowned attachment');
        }
        const ownedGroups = node.dependencies.filter(handle => groups.has(handle)).map(handle => groups.get(handle));
        const indirect = node.operations.flatMap(operation => operation.indirectBuffers);
        if (ownedGroups.length) {
            require(ownedGroups.length === 1 && node.operations.length === 1 && node.operations[0].type === 'compute' &&
                nodes.get(node.operations[0].pipeline)?.label === ownedGroups[0].program && !indirect.length,
                'slot compute command has an unexpected owner or shape');
            count(ownedGroups[0].owner, 'compute command'); variable.add(node.handle);
        } else if (indirect.length) {
            const owner = bufferOwners.get(indirect[0]), operation = node.operations[0];
            require(indirect.length === 1 && node.operations.length === 1 && operation.type === 'render' &&
                owner?.kind === 'arguments' && !owner.pending &&
                pipelineOwners.get(operation.pipeline)?.key === owner.key && node.dependencies.includes(indirect[0]) &&
                node.dependencies.includes(operation.pipeline), 'raster command has no exact slot argument/pipeline owner');
            count(owner, 'raster command'); variable.add(node.handle);
        } else require(!node.dependencies.some(handle => variable.has(handle)), 'unclassified command retains slot resources');
    }
    for (const node of nodes.values()) {
        if (node.kind === 'buffer' && [selectionLabel, argumentsLabel].includes(node.label) ||
            node.kind === 'render-pipeline' && node.label.startsWith('engine-unlit-'))
            require(variable.has(node.handle), 'a slot buffer or raster pipeline has no managed owner');
    }
    const residual = [...nodes.values()].filter(node => !variable.has(node.handle)).map(node =>
        JSON.stringify({ kind: node.kind, label: node.label, size: node.size, usage: node.usage })).sort();
    const nativePrograms = [...new Set([...nodes.values()].filter(node => node.nativeId !== null).map(node =>
        JSON.stringify({ kind: node.kind, label: node.label, nativeId: node.nativeId })))].sort();
    require(variable.size + residual.length === nodes.size, 'resource accounting is incomplete');
    return { live: nodes.size, cohortResources: [...variable].sort((left, right) => left - right),
        owners, residual, nativePrograms };
}
