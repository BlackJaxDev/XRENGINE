// Refit local-space bounds from the current GPU deformation stream (20B/vertex).
struct RefitParameters {
    meshletCount: u32,
    vertexCount: u32,
    positionWordCount: u32,
    descriptorWordOffset: u32,
    remapWordOffset: u32,
    remapCount: u32,
    boundsWordOffset: u32,
    boundsWordCount: u32,
    reserved0: u32,
    reserved1: u32,
    reserved2: u32,
    reserved3: u32,
};
@group(0) @binding(0) var<storage, read> source: array<u32>;
@group(0) @binding(1) var<storage, read> positions: array<u32>;
@group(0) @binding(2) var<storage, read_write> bounds: array<u32>;
@group(0) @binding(3) var<storage, read_write> state: array<atomic<u32>>;
@group(0) @binding(4) var<uniform> parameters: RefitParameters;
var<workgroup> lower: array<vec3<f32>, 64>;
var<workgroup> upper: array<vec3<f32>, 64>;
var<workgroup> invalid: array<u32, 64>;
var<workgroup> selected: array<u32, 4>;

fn finite3(value: vec3<f32>) -> bool {
    return all((bitcast<vec3<u32>>(value) & vec3<u32>(0x7f800000u)) != vec3<u32>(0x7f800000u));
}
fn sourceRange(offset: u32, count: u32) -> bool {
    let length = arrayLength(&source);
    return offset <= length && count <= length - min(offset, length);
}
fn fault() {
    if (arrayLength(&state) >= 8u) { atomicOr(&state[5u], 2u); }
}
fn prepareMeshlet(meshlet: u32) {
    selected[0] = 0u;
    if (meshlet >= parameters.meshletCount || arrayLength(&state) < 8u) { return; }
    if (!sourceRange(parameters.descriptorWordOffset, 0u) ||
        meshlet >= (arrayLength(&source) - min(parameters.descriptorWordOffset, arrayLength(&source))) / 20u ||
        !sourceRange(parameters.remapWordOffset, parameters.remapCount) ||
        parameters.positionWordCount > arrayLength(&positions) || parameters.vertexCount > parameters.positionWordCount / 5u ||
        parameters.boundsWordCount > arrayLength(&bounds) || parameters.boundsWordOffset > parameters.boundsWordCount ||
        meshlet >= (parameters.boundsWordCount - min(parameters.boundsWordOffset, parameters.boundsWordCount)) / 4u) {
        fault(); return;
    }
    let descriptor = parameters.descriptorWordOffset + meshlet * 20u;
    let vertexOffset = source[descriptor + 4u];
    let vertexCount = source[descriptor + 6u];
    if (vertexCount == 0u || vertexCount > 64u || vertexOffset > parameters.remapCount ||
        vertexCount > parameters.remapCount - min(vertexOffset, parameters.remapCount)) { fault(); return; }
    selected[0] = 1u;
    selected[1] = parameters.remapWordOffset + vertexOffset;
    selected[2] = vertexCount;
    selected[3] = parameters.boundsWordOffset + meshlet * 4u;
}
@compute @workgroup_size(64, 1, 1)
fn meshletsRefitBounds(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) local: u32) {
    let meshlet = group.x + group.y * 65535u;
    if (local == 0u) { prepareMeshlet(meshlet); }
    workgroupBarrier();
    lower[local] = vec3<f32>(3.402823466e+38);
    upper[local] = vec3<f32>(-3.402823466e+38);
    invalid[local] = 0u;
    if (selected[0] != 0u && local < selected[2]) {
        let vertex = source[selected[1] + local];
        if (vertex >= parameters.vertexCount) {
            fault(); invalid[local] = 1u;
        } else {
            let offset = vertex * 5u;
            let position = bitcast<vec3<f32>>(vec3<u32>(positions[offset], positions[offset + 1u], positions[offset + 2u]));
            if (finite3(position)) {
                lower[local] = position; upper[local] = position;
            } else { invalid[local] = 1u; }
        }
    }
    workgroupBarrier();
    for (var stride = 32u; stride > 0u; stride /= 2u) {
        if (local < stride) {
            lower[local] = min(lower[local], lower[local + stride]);
            upper[local] = max(upper[local], upper[local + stride]);
            invalid[local] |= invalid[local + stride];
        }
        workgroupBarrier();
    }
    if (local == 0u && selected[0] != 0u) {
        var sphere = vec4<f32>(0.0, 0.0, 0.0, -1.0);
        let center = lower[0] * 0.5 + upper[0] * 0.5;
        let extent = max(abs(upper[0] - center), abs(center - lower[0]));
        let radius = length(extent) * 1.00001;
        if (invalid[0] == 0u && finite3(center) && finite3(extent) &&
            (bitcast<u32>(radius) & 0x7f800000u) != 0x7f800000u) { sphere = vec4<f32>(center, radius); }
        let words = bitcast<vec4<u32>>(sphere);
        bounds[selected[3]] = words.x;
        bounds[selected[3] + 1u] = words.y;
        bounds[selected[3] + 2u] = words.z;
        bounds[selected[3] + 3u] = words.w;
    }
}
