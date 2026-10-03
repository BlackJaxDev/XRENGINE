struct FinalizeParameters {
    meshletCount: u32,
    indexCapacity: u32,
    sourceTriangleCount: u32,
    reserved0: u32,
};
@group(0) @binding(0) var<storage, read_write> state: array<atomic<u32>>;
@group(0) @binding(1) var<uniform> parameters: FinalizeParameters;
@compute @workgroup_size(1, 1, 1)
fn meshletsFinalizeIndexed() {
    // Initialize every indirect word, including firstInstance, even on faults.
    for (var index = 0u; index < min(5u, arrayLength(&state)); index++) { atomicStore(&state[index], 0u); }
    if (arrayLength(&state) < 8u) { return; }
    if (parameters.sourceTriangleCount > 0x3fffffffu ||
        parameters.sourceTriangleCount * 3u > parameters.indexCapacity) { atomicOr(&state[5u], 1u); }
    if (atomicLoad(&state[6u]) != parameters.meshletCount ||
        atomicLoad(&state[7u]) > parameters.sourceTriangleCount) { atomicOr(&state[5u], 2u); }
    if (atomicLoad(&state[5u]) != 0u || atomicLoad(&state[7u]) == 0u) { return; }
    atomicStore(&state[0u], parameters.sourceTriangleCount * 3u);
    atomicStore(&state[1u], 1u);
}
