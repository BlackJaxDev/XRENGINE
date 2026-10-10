// Emit one indexed indirect record per rank, with only this source's rank live.
struct MaskParameters {
    sourceIndex: u32,
    sourceCount: u32,
    rankCount: u32,
    reserved: u32,
};
@group(0) @binding(0) var<storage, read> ranks: array<u32>;
@group(0) @binding(1) var<storage, read> originalArguments: array<u32>;
@group(0) @binding(2) var<storage, read_write> rankedArguments: array<u32>;
@group(0) @binding(3) var<uniform> parameters: MaskParameters;

@compute @workgroup_size(64, 1, 1)
fn authoredMaskRankedArguments(@builtin(global_invocation_id) invocation: vec3<u32>) {
    let rank = invocation.x;
    if (parameters.sourceCount > 64u || parameters.rankCount > 64u ||
        parameters.sourceIndex >= parameters.sourceCount || rank >= parameters.rankCount ||
        arrayLength(&ranks) < parameters.sourceCount || arrayLength(&originalArguments) < 5u ||
        arrayLength(&rankedArguments) / 5u < parameters.rankCount) { return; }
    let offset = rank * 5u;
    let selected = rank == ranks[parameters.sourceIndex];
    rankedArguments[offset] = select(0u, originalArguments[0u], selected);
    rankedArguments[offset + 1u] = select(0u, originalArguments[1u], selected);
    rankedArguments[offset + 2u] = originalArguments[2u];
    rankedArguments[offset + 3u] = originalArguments[3u];
    rankedArguments[offset + 4u] = originalArguments[4u];
}
