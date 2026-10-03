// Backend port of GpuBvhTree's morton_codes.comp, sort_morton.comp and bvh_build.comp.
// The native 16-byte header and 48-byte compact nodes remain unchanged.
struct MortonObject { code: u32, objectId: u32 }
struct BuildFrame {
    count: u32, paddedCount: u32, viewCount: u32, padding: u32,
    sceneMin: vec4<f32>, sceneRange: vec4<f32>, reserved: vec4<u32>,
}
struct SortStage { width: u32, distance: u32, padding0: u32, padding1: u32 }
@group(0) @binding(0) var<storage, read> records: array<u32>;
@group(0) @binding(1) var<storage, read_write> morton: array<MortonObject>;
@group(0) @binding(2) var<storage, read_write> nodes: array<atomic<u32>>;
@group(0) @binding(3) var<storage, read_write> faults: array<atomic<u32>>;
@group(0) @binding(4) var<uniform> frame: BuildFrame;
@group(0) @binding(5) var<uniform> stage: SortStage;
const invalidIndex = 0xffffffffu;
const nodeStride = 12u;

fn nodeBase(index: u32) -> u32 { return 4u + index * nodeStride; }
fn minimum(index: u32) -> vec3<f32> {
    let at = index * 40u + 4u;
    return vec3<f32>(bitcast<f32>(records[at]), bitcast<f32>(records[at + 1u]), bitcast<f32>(records[at + 2u]));
}
fn maximum(index: u32) -> vec3<f32> {
    let at = index * 40u + 8u;
    return vec3<f32>(bitcast<f32>(records[at]), bitcast<f32>(records[at + 1u]), bitcast<f32>(records[at + 2u]));
}
fn storeBounds(index: u32, low: vec3<f32>, high: vec3<f32>) {
    let at = nodeBase(index);
    atomicStore(&nodes[at], bitcast<u32>(low.x)); atomicStore(&nodes[at + 1u], bitcast<u32>(low.y)); atomicStore(&nodes[at + 2u], bitcast<u32>(low.z));
    atomicStore(&nodes[at + 4u], bitcast<u32>(high.x)); atomicStore(&nodes[at + 5u], bitcast<u32>(high.y)); atomicStore(&nodes[at + 6u], bitcast<u32>(high.z));
}
fn expandBits(input: u32) -> u32 {
    var value = (input * 0x00010001u) & 0xff0000ffu;
    value = (value * 0x00000101u) & 0x0f00f00fu;
    value = (value * 0x00000011u) & 0xc30c30c3u;
    return (value * 0x00000005u) & 0x49249249u;
}

@compute @workgroup_size(64)
fn mortonMain(@builtin(global_invocation_id) id: vec3<u32>) {
    let index = id.x;
    if (index == 0u) { for (var i = 0u; i < 4u; i++) { atomicStore(&faults[i], 0u); } }
    if (index >= frame.paddedCount) { return; }
    if (index >= frame.count) { morton[index] = MortonObject(invalidIndex, invalidIndex); return; }
    let center = minimum(index) * 0.5 + maximum(index) * 0.5;
    let normalized = clamp((center - frame.sceneMin.xyz) / frame.sceneRange.xyz, vec3<f32>(0.0), vec3<f32>(1.0));
    let grid = vec3<u32>(normalized * 1023.0);
    let code = expandBits(grid.x) | (expandBits(grid.y) << 1u) | (expandBits(grid.z) << 2u);
    morton[index] = MortonObject(code, index);
}

fn greater(a: MortonObject, b: MortonObject) -> bool {
    return a.code > b.code || (a.code == b.code && a.objectId > b.objectId);
}

// WebGPU dispatch boundaries replace the native sort's 1024-thread workgroup barriers.
@compute @workgroup_size(64)
fn sortMain(@builtin(global_invocation_id) id: vec3<u32>) {
    let index = id.x; let other = index ^ stage.distance;
    if (index >= frame.paddedCount || other >= frame.paddedCount || other <= index) { return; }
    let a = morton[index]; let b = morton[other];
    let ascending = (index & stage.width) == 0u;
    if (select(greater(b, a), greater(a, b), ascending)) { morton[index] = b; morton[other] = a; }
}

fn prefix(first: i32, other: i32) -> i32 {
    if (other < 0 || other >= i32(frame.count)) { return -1; }
    let a = morton[u32(first)].code; let b = morton[u32(other)].code;
    if (a == b) { return 32 + i32(countLeadingZeros(u32(first) ^ u32(other))); }
    return i32(countLeadingZeros(a ^ b));
}
fn determineRange(index: i32) -> vec2<i32> {
    let direction = select(-1, 1, prefix(index, index + 1) >= prefix(index, index - 1));
    let baseline = prefix(index, index - direction);
    var maximumLength = 2;
    loop {
        if (maximumLength > i32(frame.count) * 2 || prefix(index, index + maximumLength * direction) <= baseline) { break; }
        maximumLength *= 2;
    }
    var length = 0;
    var step = maximumLength / 2;
    loop {
        if (step < 1) { break; }
        if (prefix(index, index + (length + step) * direction) > baseline) { length += step; }
        step /= 2;
    }
    let last = index + length * direction;
    return vec2<i32>(min(index, last), max(index, last));
}
fn findSplit(first: i32, last: i32) -> i32 {
    let common = prefix(first, last);
    var split = first; var step = last - first;
    loop {
        step = (step + 1) >> 1;
        let candidate = split + step;
        if (candidate < last && prefix(first, candidate) > common) { split = candidate; }
        if (step <= 1) { break; }
    }
    return split;
}

@compute @workgroup_size(64)
fn initializeMain(@builtin(global_invocation_id) id: vec3<u32>) {
    let index = id.x; let total = frame.count * 2u - 1u;
    if (index == 0u) {
        atomicStore(&nodes[0], total); atomicStore(&nodes[1], 0u);
        atomicStore(&nodes[2], nodeStride); atomicStore(&nodes[3], 1u);
    }
    if (index >= total) { return; }
    let at = nodeBase(index);
    storeBounds(index, vec3<f32>(3.0e38), vec3<f32>(-3.0e38));
    atomicStore(&nodes[at + 3u], invalidIndex); atomicStore(&nodes[at + 7u], invalidIndex);
    atomicStore(&nodes[at + 8u], index); atomicStore(&nodes[at + 9u], 1u);
    atomicStore(&nodes[at + 10u], invalidIndex); atomicStore(&nodes[at + 11u], select(0u, 1u, index < frame.count));
}

@compute @workgroup_size(64)
fn connectMain(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x + 1u >= frame.count) { return; }
    let range = determineRange(i32(id.x));
    let split = findSplit(range.x, range.y);
    let left = select(frame.count + u32(split), u32(split), split == range.x);
    let right = select(frame.count + u32(split + 1), u32(split + 1), split + 1 == range.y);
    let at = nodeBase(frame.count + id.x);
    atomicStore(&nodes[at + 3u], left); atomicStore(&nodes[at + 7u], right);
    atomicStore(&nodes[at + 8u], u32(range.x)); atomicStore(&nodes[at + 9u], u32(range.y - range.x + 1));
}

fn assignParent(child: u32, parent: u32) {
    if (child >= frame.count * 2u - 1u) { atomicOr(&faults[0], 8u); return; }
    let at = nodeBase(child) + 10u;
    for (var attempt = 0u; attempt < 32u; attempt++) {
        let result = atomicCompareExchangeWeak(&nodes[at], invalidIndex, parent);
        if (result.exchanged || result.old_value == parent) { return; }
        if (result.old_value != invalidIndex) { atomicOr(&faults[0], 8u); return; }
    }
    atomicOr(&faults[0], 8u);
}

@compute @workgroup_size(64)
fn parentsMain(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x + 1u >= frame.count) { return; }
    let index = frame.count + id.x; let at = nodeBase(index);
    assignParent(atomicLoad(&nodes[at + 3u]), index); assignParent(atomicLoad(&nodes[at + 7u]), index);
}

@compute @workgroup_size(1)
fn rootMain() {
    var current = 0u; let total = frame.count * 2u - 1u;
    for (var guard = 0u; guard < total; guard++) {
        let parent = atomicLoad(&nodes[nodeBase(current) + 10u]);
        if (parent == invalidIndex) { atomicStore(&nodes[1], current); return; }
        if (parent >= total) { break; }
        current = parent;
    }
    atomicOr(&faults[0], 8u);
}

// Native refit propagates via cross-workgroup release/acquire assumptions.
// WebGPU instead reduces each canonical primitive range independently. The
// topology and resulting bounds are identical, with no cross-workgroup spin.
@compute @workgroup_size(64)
fn refitMain(@builtin(global_invocation_id) id: vec3<u32>) {
    let index = id.x;
    if (index >= frame.count * 2u - 1u || atomicLoad(&faults[0]) != 0u) { return; }
    let at = nodeBase(index); let first = atomicLoad(&nodes[at + 8u]); let count = atomicLoad(&nodes[at + 9u]);
    if (count == 0u || first >= frame.count || count > frame.count - first) { atomicOr(&faults[0], 8u); return; }
    var low = vec3<f32>(3.0e38); var high = vec3<f32>(-3.0e38);
    for (var i = 0u; i < count; i++) {
        let objectId = morton[first + i].objectId;
        if (objectId >= frame.count) { atomicOr(&faults[0], 8u); return; }
        low = min(low, minimum(objectId)); high = max(high, maximum(objectId));
    }
    storeBounds(index, low, high);
}
