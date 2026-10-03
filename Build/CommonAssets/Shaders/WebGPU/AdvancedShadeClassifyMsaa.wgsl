// Union all four raw samples into one membership per exact texture/kernel cohort and tile.
// The final cohort is reserved for invalid identities and unbound material rows.
struct ClassificationParameters {
    screenExtent: vec2<u32>,
    tilesCount: vec2<u32>,
    viewIndex: u32,
    cohortCount: u32,
    tileCapacity: u32,
    reserved: u32,
};
@group(0) @binding(0) var<storage, read> scene: array<u32>;
@group(0) @binding(1) var<storage, read> materialGroups: array<u32>;
@group(0) @binding(2) var<storage, read_write> tiles: array<u32>;
@group(0) @binding(3) var<storage, read_write> counts: array<atomic<u32>>;
@group(0) @binding(4) var<uniform> parameters: ClassificationParameters;
@group(0) @binding(5) var rawVisibilityIdentity: texture_multisampled_2d<u32>;
@group(0) @binding(6) var rawVisibilityMetadataSelection: texture_multisampled_2d<u32>;
var<workgroup> membership: array<atomic<u32>, 4>;
var<workgroup> kernels: array<atomic<u32>, 128>;
var<workgroup> flags: array<atomic<u32>, 128>;
const INVALID = 0xffffffffu;
fn row(table: u32, dense: u32) -> u32 {
    let base = table * 8u;
    if (dense >= scene[base + 1u]) { return INVALID; }
    return scene[base] + dense * scene[base + 2u];
}
fn resolve(table: u32, handle: vec2<u32>) -> u32 {
    let base = table * 8u;
    if (handle.x == 0u || handle.y == 0u || handle.x >= scene[base + 4u]) { return INVALID; }
    let lookup = scene[base + 3u] + handle.x * 2u;
    if (scene[lookup] != handle.y || row(table, scene[lookup + 1u]) == INVALID) { return INVALID; }
    return scene[lookup + 1u];
}
fn handle(word: u32) -> vec2<u32> { return vec2<u32>(scene[word], scene[word + 1u]); }
fn materialAndKernel(identity: vec2<u32>, metadata: u32) -> vec2<u32> {
    if (identity.x == 0u || identity.x == INVALID || identity.y == INVALID || metadata == INVALID ||
        ((metadata >> 16u) & 255u) != 1u || (metadata & 7u) > 4u ||
        ((metadata >> 8u) & 255u) != parameters.viewIndex || identity.x >= scene[4u]) { return vec2<u32>(INVALID); }
    let lookup = scene[3u] + identity.x * 2u;
    if (scene[lookup] == 0u) { return vec2<u32>(INVALID); }
    let draw = row(0u, scene[lookup + 1u]);
    if (draw == INVALID) { return vec2<u32>(INVALID); }
    let materialDense = resolve(3u, handle(draw + 4u));
    let material = row(3u, materialDense);
    if (material == INVALID) { return vec2<u32>(INVALID); }
    let kernelDense = resolve(21u, handle(material + 2u));
    let kernel = row(21u, kernelDense);
    if (kernel == INVALID || any(handle(material + 4u) != handle(kernel + 2u)) ||
        scene[material + 7u] >= 32u || (scene[kernel + 5u] & (1u << scene[material + 7u])) == 0u) {
        return vec2<u32>(INVALID);
    }
    return vec2<u32>(materialDense, kernelDense);
}
fn unpackVisibilityPair(value: vec4<u32>) -> vec2<u32> {
    return vec2<u32>(value.x | (value.y << 16u), value.z | (value.w << 16u));
}
@compute @workgroup_size(16, 16, 1)
fn advancedShadeClassifyMsaa(@builtin(workgroup_id) group: vec3<u32>,
    @builtin(local_invocation_id) local: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    if (lane < 4u) { atomicStore(&membership[lane], 0u); }
    if (lane < 128u) { atomicStore(&kernels[lane], INVALID); atomicStore(&flags[lane], 0u); }
    workgroupBarrier();
    let pixel = group.xy * 16u + local.xy;
    if (parameters.cohortCount > 0u && parameters.cohortCount <= 128u && all(pixel < parameters.screenExtent)) {
        for (var sampleIndex = 0u; sampleIndex < 4u; sampleIndex++) {
            let identity = unpackVisibilityPair(textureLoad(rawVisibilityIdentity, pixel, sampleIndex));
            if (identity.x != 0u && identity.x != INVALID) {
                let metadata = unpackVisibilityPair(textureLoad(rawVisibilityMetadataSelection, pixel, sampleIndex)).x;
                let resolved = materialAndKernel(identity, metadata);
                var cohort = parameters.cohortCount - 1u;
                var kernel = INVALID;
                var tileFlags = 0u;
                if (resolved.x < arrayLength(&materialGroups) && materialGroups[resolved.x] < cohort) {
                    cohort = materialGroups[resolved.x];
                    kernel = resolved.y;
                    let kernelRow = row(21u, kernel);
                    tileFlags = select(0u, 4u, (scene[kernelRow + 4u] & 2048u) != 0u);
                }
                atomicOr(&membership[cohort / 32u], 1u << (cohort & 31u));
                atomicStore(&kernels[cohort], kernel);
                atomicOr(&flags[cohort], tileFlags);
            }
        }
    }
    workgroupBarrier();
    if (lane >= parameters.cohortCount || lane >= 128u || (atomicLoad(&membership[lane / 32u]) & (1u << (lane & 31u))) == 0u) { return; }
    if (lane * 4u + 3u >= arrayLength(&counts)) { return; }
    let index = atomicAdd(&counts[lane * 4u], 1u);
    let word = (lane * parameters.tileCapacity + index) * 4u;
    if (index >= parameters.tileCapacity || word > arrayLength(&tiles) || arrayLength(&tiles) - word < 4u ||
        group.x > 65535u || group.y > 65535u) {
        atomicOr(&counts[lane * 4u + 1u], 1u); return;
    }
    tiles[word] = group.x | (group.y << 16u);
    tiles[word + 1u] = parameters.viewIndex;
    tiles[word + 2u] = atomicLoad(&kernels[lane]);
    tiles[word + 3u] = atomicLoad(&flags[lane]);
}
