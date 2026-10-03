const unavailable = Object.freeze({
    ComputeSkinning: 'Select skinning=Compute separately from the scene visibility strategy.',
    Meshlet: 'The selected browser profile has no meshlet submission implementation.'
});

/** Scene strategy selection is separate from the low-level command capabilities. */
export function selectBrowserSubmissionStrategy(requested = 'Auto') {
    if (!['Auto', 'CpuDirect', 'GpuIndirect', 'ComputeCulling', 'HiZ', 'BvhCulling', 'BvhHiZ'].includes(requested)) {
        const reason = Object.hasOwn(unavailable, requested) ? unavailable[requested] : 'Unknown browser scene submission strategy.';
        throw new Error(`Required submission strategy ${String(requested)} is unavailable: ${reason}`);
    }
    const selected = requested === 'Auto' ? 'CpuDirect' : requested;
    return Object.freeze({ requested, selected, gpu: selected !== 'CpuDirect',
        culling: ['ComputeCulling', 'HiZ', 'BvhCulling', 'BvhHiZ'].includes(selected),
        hiZ: selected === 'HiZ' || selected === 'BvhHiZ', hierarchy: selected === 'BvhCulling' || selected === 'BvhHiZ',
        visibilityAlgorithm: selected === 'BvhCulling' || selected === 'BvhHiZ' ? 'canonical-karras-lbvh'
            : selected === 'ComputeCulling' || selected === 'HiZ' ? 'flat-gpu-aabb' : 'none',
        experimental: selected !== 'CpuDirect',
        reason: requested === 'Auto' ? 'Automatic selection retains CPU-direct until device correctness and total-frame-cost qualification.'
            : selected === 'CpuDirect' ? 'CPU-direct scene submission was explicitly requested.'
            : 'Explicit experimental GPU path; correctness and mobile performance qualification are pending.',
        permittedAutomatic: Object.freeze(['CpuDirect']), unavailable });
}
