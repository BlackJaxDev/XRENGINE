const unavailable = Object.freeze({
    ComputeCulling: 'Compute culling is available only in the offscreen reference; scene integration and device qualification are pending.',
    GpuIndirect: 'The command API supports indirect draws, but the focused scene pipeline does not submit them.',
    ComputeSkinning: 'Compute skinning needs CPU/GPU parity and physical-device cost evidence before it can be selected.',
    HiZ: 'Hierarchical visibility needs qualified depth reductions, conservative bounds and resize handling.',
    Meshlet: 'The selected browser profile has no meshlet submission implementation.'
});

/** Scene strategy selection is separate from the low-level command capabilities. */
export function selectBrowserSubmissionStrategy(requested = 'Auto') {
    if (requested !== 'Auto' && requested !== 'CpuDirect') {
        const reason = Object.hasOwn(unavailable, requested) ? unavailable[requested] : 'Unknown browser scene submission strategy.';
        throw new Error(`Required submission strategy ${String(requested)} is unavailable: ${reason}`);
    }
    return Object.freeze({ requested, selected: 'CpuDirect',
        reason: requested === 'Auto' ? 'Automatic selection permits only the implemented CPU-direct scene pipeline.' : 'CPU-direct scene submission was explicitly requested.',
        permittedAutomatic: Object.freeze(['CpuDirect']), unavailable });
}
