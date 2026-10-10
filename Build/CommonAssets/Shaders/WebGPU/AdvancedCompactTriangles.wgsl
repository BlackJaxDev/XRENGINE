// GPU-only conservative visibility and indexed/meshlet triangle expansion.
// One count-written drawIndirect consumes each compatible raster-state bucket.
struct CompactionParameters {
    viewProjectionUnjittered: mat4x4<f32>,
    payloadCount: u32,
    triangleCapacity: u32,
    cullMode: u32,
    coverage: u32,
    rasterStateClass: u32,
    producer: u32,
    viewId: u32,
    triangleBase: u32,
    argumentBase: u32,
    bucketIndex: u32,
    reserved1: u32,
    reserved2: u32,
};
@group(0) @binding(0) var<storage, read> scene: array<u32>;
@group(0) @binding(1) var<storage, read> geometry: array<u32>;
@group(0) @binding(2) var<storage, read> payloads: array<u32>;
@group(0) @binding(3) var<storage, read> candidates: array<u32>;
@group(0) @binding(4) var<storage, read> producers: array<u32>;
@group(0) @binding(5) var<storage, read_write> triangles: array<u32>;
@group(0) @binding(6) var<storage, read_write> arguments: array<atomic<u32>>;
@group(0) @binding(7) var<uniform> parameters: CompactionParameters;
var<workgroup> selected: array<u32, 8>;
const INVALID = 0xffffffffu;
fn sceneRow(table: u32, index: u32) -> u32 {
    let directory = table * 8u;
    if (table >= 32u || index >= scene[directory + 1u]) { return INVALID; }
    return scene[directory] + index * scene[directory + 2u];
}
fn resolveHandle(table: u32, handle: vec2<u32>) -> u32 {
    let directory = table * 8u;
    if (handle.x == 0u || handle.y == 0u || handle.x >= scene[directory + 4u]) { return INVALID; }
    let lookup = scene[directory + 3u] + handle.x * 2u;
    if (scene[lookup] != handle.y) { return INVALID; }
    return sceneRow(table, scene[lookup + 1u]);
}
fn insidePlane(sphere: vec4<f32>, plane: vec4<f32>) -> bool {
    let normalLength = length(plane.xyz);
    return normalLength > 0.0 && dot(plane, vec4<f32>(sphere.xyz, 1.0)) >= -sphere.w * normalLength;
}
fn insideFrustum(sphere: vec4<f32>) -> bool {
    if (any((bitcast<vec4<u32>>(sphere) & vec4<u32>(0x7f800000u)) == vec4<u32>(0x7f800000u)) || sphere.w < 0.0) { return false; }
    // Uniform packing transposes engine row-vector matrices into WGSL columns.
    let m = transpose(parameters.viewProjectionUnjittered);
    return insidePlane(sphere, m[3] + m[0]) && insidePlane(sphere, m[3] - m[0]) &&
        insidePlane(sphere, m[3] + m[1]) && insidePlane(sphere, m[3] - m[1]) &&
        insidePlane(sphere, m[2]) && insidePlane(sphere, m[3] - m[2]);
}
fn includesView(candidate: u32) -> bool {
    if (parameters.viewId < 32u) { return (candidates[candidate + 16u] & (1u << parameters.viewId)) != 0u; }
    if (parameters.viewId < 64u) { return (candidates[candidate + 17u] & (1u << (parameters.viewId - 32u))) != 0u; }
    return false;
}
fn reserveTriangles(count: u32) -> u32 {
    if (count > parameters.triangleCapacity || count > 0x55555555u) {
        atomicOr(&arguments[parameters.argumentBase + 4u], 1u); return INVALID;
    }
    let vertices = count * 3u;
    var observed = atomicLoad(&arguments[parameters.argumentBase]);
    loop {
        if (observed > parameters.triangleCapacity * 3u - vertices) {
            atomicOr(&arguments[parameters.argumentBase + 4u], 1u); return INVALID;
        }
        let exchange = atomicCompareExchangeWeak(&arguments[parameters.argumentBase], observed, observed + vertices);
        if (exchange.exchanged) { return observed / 3u; }
        observed = exchange.old_value;
    }
}
fn meshletAddress(index: u32) -> u32 {
    if (index >= geometry[17u] / 80u) { return INVALID; }
    return geometry[16u] + index * 20u;
}
fn meshletRangesValid(mesh: u32, descriptor: u32) -> bool {
    let vertexBase = scene[mesh + 36u];
    let vertexCount = scene[mesh + 37u];
    let wordBase = scene[mesh + 44u];
    let wordCount = scene[mesh + 45u];
    if (vertexBase > geometry[21u] / 4u || vertexCount > geometry[21u] / 4u - vertexBase ||
        wordBase > geometry[25u] / 4u || wordCount > geometry[25u] / 4u - wordBase) { return false; }
    let byteCount = wordCount * 4u;
    let vertexOffset = geometry[descriptor + 4u];
    let byteOffset = geometry[descriptor + 5u];
    return vertexOffset <= vertexCount && geometry[descriptor + 6u] <= vertexCount - vertexOffset &&
        byteOffset <= byteCount && geometry[descriptor + 7u] <= (byteCount - byteOffset) / 3u;
}
fn selectDraw(payloadIndex: u32) {
    selected[0] = INVALID;
    if (payloadIndex >= parameters.payloadCount || payloadIndex >= arrayLength(&payloads) / 24u ||
        payloadIndex >= arrayLength(&candidates) / 20u || payloadIndex >= arrayLength(&producers) / 2u ||
        parameters.triangleCapacity > 0x55555555u) { return; }
    let payload = payloadIndex * 24u;
    let candidate = payloadIndex * 20u;
    let producer = producers[payloadIndex * 2u];
    if (producers[payloadIndex * 2u + 1u] != parameters.bucketIndex ||
        producer != parameters.producer || producer > 4u || payloads[payload + 18u] != parameters.rasterStateClass ||
        payloads[payload + 19u] != parameters.coverage || payloads[payload + 20u] != parameters.cullMode) { return; }
    if (!includesView(candidate)) { return; }
    let sphere = bitcast<vec4<f32>>(vec4<u32>(candidates[candidate + 4u], candidates[candidate + 5u], candidates[candidate + 6u], candidates[candidate + 7u]));
    // Authored deformation without a proven envelope remains conservatively visible.
    if ((candidates[candidate + 19u] & 24u) == 0u && !insideFrustum(sphere)) { return; }
    let draw = resolveHandle(0u, vec2<u32>(payloads[payload], payloads[payload + 1u]));
    let mesh = resolveHandle(2u, vec2<u32>(payloads[payload + 2u], payloads[payload + 3u]));
    let material = resolveHandle(3u, vec2<u32>(payloads[payload + 4u], payloads[payload + 5u]));
    if (draw == INVALID || mesh == INVALID || material == INVALID ||
        candidates[candidate] != payloads[payload] || candidates[candidate + 1u] != payloads[payload + 1u]) {
        atomicOr(&arguments[parameters.argumentBase + 4u], 2u); return;
    }
    var count = payloads[payload + 16u] / 3u;
    let meshlet = producer <= 1u;
    if (meshlet) {
        count = 0u;
        let first = payloads[payload + 11u];
        let meshletCount = payloads[payload + 12u];
        if (meshletCount == 0u || first > 0x00ffffffu || meshletCount > 0x01000000u - first ||
            first < scene[mesh + 54u] || first - scene[mesh + 54u] > scene[mesh + 55u] ||
            meshletCount > scene[mesh + 55u] - (first - scene[mesh + 54u])) {
            atomicOr(&arguments[parameters.argumentBase + 4u], 2u); return;
        }
        for (var index = 0u; index < meshletCount; index++) {
            let address = meshletAddress(first + index);
            if (address == INVALID || !meshletRangesValid(mesh, address) || geometry[address + 7u] > 256u || geometry[address + 7u] > parameters.triangleCapacity - min(count, parameters.triangleCapacity)) {
                atomicOr(&arguments[parameters.argumentBase + 4u], 2u); return;
            }
            count += geometry[address + 7u];
        }
    } else if (payloads[payload + 15u] % 3u != 0u || payloads[payload + 16u] % 3u != 0u) {
        atomicOr(&arguments[parameters.argumentBase + 4u], 2u); return;
    }
    if (count == 0u) { return; }
    let firstTriangle = reserveTriangles(count);
    if (firstTriangle == INVALID) { return; }
    selected[0] = firstTriangle;
    selected[1] = count;
    selected[2] = producer;
    selected[3] = select(0u, 2u, (payloads[payload + 22u] & 1u) != 0u);
    selected[4] = payload;
    selected[5] = (material - scene[24u]) / scene[26u];
    selected[6] = payloadIndex;
    selected[7] = mesh;
}
fn emitTriangle(output: u32, primitive: u32, vertices: vec3<u32>) {
    let destination = parameters.triangleBase + selected[0] + output;
    if (destination >= arrayLength(&triangles) / 8u) {
        atomicOr(&arguments[parameters.argumentBase + 4u], 1u); return;
    }
    let vertexBase = payloads[selected[4] + 6u];
    let absolute = vertices + vec3<u32>(vertexBase);
    let stream = selected[3];
    if (primitive == INVALID || any(vertices >= vec3<u32>(payloads[selected[4] + 17u])) ||
        any(absolute < vertices) || any(absolute >= vec3<u32>(geometry[stream * 4u + 1u] / 64u))) {
        atomicOr(&arguments[parameters.argumentBase + 4u], 2u);
        triangles[destination * 8u] = INVALID;
        return;
    }
    let word = destination * 8u;
    triangles[word] = selected[6];
    triangles[word + 1u] = primitive;
    triangles[word + 2u] = selected[2];
    triangles[word + 3u] = stream;
    triangles[word + 4u] = absolute.x;
    triangles[word + 5u] = absolute.y;
    triangles[word + 6u] = absolute.z;
    triangles[word + 7u] = selected[5];
}
fn triangleByte(offset: u32) -> u32 {
    if (offset >= geometry[25u]) { return INVALID; }
    return (geometry[geometry[24u] + offset / 4u] >> ((offset % 4u) * 8u)) & 255u;
}
@compute @workgroup_size(64, 1, 1)
fn advancedCompactTriangles(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) local: u32) {
    let payloadIndex = group.x + group.y * 65535u;
    if (local == 0u) { selectDraw(payloadIndex); }
    workgroupBarrier();
    if (selected[0] == INVALID) { return; }
    let payload = selected[4];
    if (selected[2] > 1u) {
        let firstIndex = payloads[payload + 15u];
        for (var primitive = local; primitive < selected[1]; primitive += 64u) {
            let index = firstIndex + primitive * 3u;
            if (index < firstIndex || index > geometry[5u] / 4u || geometry[5u] / 4u - index < 3u) {
                atomicOr(&arguments[parameters.argumentBase + 4u], 2u); continue;
            }
            let base = geometry[4u] + index;
            emitTriangle(primitive, firstIndex / 3u + primitive, vec3<u32>(geometry[base], geometry[base + 1u], geometry[base + 2u]));
        }
    } else {
        var offset = 0u;
        for (var meshlet = 0u; meshlet < payloads[payload + 12u]; meshlet++) {
            let meshletIndex = payloads[payload + 11u] + meshlet;
            let descriptor = meshletAddress(meshletIndex);
            let count = geometry[descriptor + 7u];
            for (var triangle = local; triangle < count; triangle += 64u) {
                let byte = scene[selected[7] + 44u] * 4u + geometry[descriptor + 5u] + triangle * 3u;
                let localVertices = vec3<u32>(triangleByte(byte), triangleByte(byte + 1u), triangleByte(byte + 2u));
                let remap = localVertices + vec3<u32>(scene[selected[7] + 36u] + geometry[descriptor + 4u]);
                if (any(localVertices >= vec3<u32>(geometry[descriptor + 6u])) ||
                    any(remap < localVertices) || any(remap >= vec3<u32>(geometry[21u] / 4u))) {
                    atomicOr(&arguments[parameters.argumentBase + 4u], 2u); continue;
                }
                emitTriangle(offset + triangle, (meshletIndex << 8u) | triangle,
                    vec3<u32>(geometry[geometry[20u] + remap.x], geometry[geometry[20u] + remap.y], geometry[geometry[20u] + remap.z]));
            }
            offset += count;
        }
    }
}
