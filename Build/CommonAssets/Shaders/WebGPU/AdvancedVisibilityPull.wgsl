// Vertex-pulled lowering of Advanced's producer-neutral visibility ABI.
// A GPU triangle stream replaces fragment primitive-index and task/mesh stages.
struct VisibilityParameters {
    viewProjection: mat4x4<f32>,
    viewProjectionUnjittered: mat4x4<f32>,
    previousViewProjectionUnjittered: mat4x4<f32>,
    triangleBase: u32,
    viewIndex: u32,
    viewFlags: u32,
    origin: u32,
    baseColorWord: u32,
    alphaCutoffWord: u32,
    materialFlagsWord: u32,
    masked: u32,
    directPayloadIndex: u32,
    direct: u32,
    reserved0: u32,
    reserved1: u32,
};
@group(0) @binding(0) var<storage, read> scene: array<u32>;
@group(0) @binding(1) var<storage, read> geometry: array<u32>;
@group(0) @binding(2) var<storage, read> payloads: array<u32>;
@group(0) @binding(3) var<storage, read> triangles: array<u32>;
@group(0) @binding(4) var<uniform> parameters: VisibilityParameters;
@group(0) @binding(5) var coverageTexture: texture_2d<f32>;
@group(0) @binding(6) var coverageSampler: sampler;
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
fn loadHandle(word: u32) -> vec2<u32> { return vec2<u32>(scene[word], scene[word + 1u]); }
fn loadMatrix(word: u32) -> mat4x4<f32> {
    return mat4x4<f32>(
        bitcast<vec4<f32>>(vec4<u32>(scene[word], scene[word + 1u], scene[word + 2u], scene[word + 3u])),
        bitcast<vec4<f32>>(vec4<u32>(scene[word + 4u], scene[word + 5u], scene[word + 6u], scene[word + 7u])),
        bitcast<vec4<f32>>(vec4<u32>(scene[word + 8u], scene[word + 9u], scene[word + 10u], scene[word + 11u])),
        bitcast<vec4<f32>>(vec4<u32>(scene[word + 12u], scene[word + 13u], scene[word + 14u], scene[word + 15u])));
}
fn vertexAddress(stream: u32, vertex: u32) -> u32 {
    if (stream >= 7u || vertex >= geometry[stream * 4u + 1u] / 64u) { return INVALID; }
    return geometry[stream * 4u] + vertex * 16u;
}
fn positionAt(address: u32) -> vec3<f32> {
    return bitcast<vec3<f32>>(vec3<u32>(geometry[address], geometry[address + 1u], geometry[address + 2u]));
}
fn finite4(value: vec4<f32>) -> bool {
    return all((bitcast<vec4<u32>>(value) & vec4<u32>(0x7f800000u)) != vec4<u32>(0x7f800000u));
}
struct VisibilityVertex {
    @builtin(position) position: vec4<f32>,
    @location(0) @interpolate(flat) identity: vec2<u32>,
    @location(1) @interpolate(flat) metadata: u32,
    @location(2) @interpolate(flat) selection: u32,
    @location(3) @interpolate(flat) material: u32,
    @location(4) uv: vec2<f32>,
};
fn rejectedVertex() -> VisibilityVertex {
    return VisibilityVertex(vec4<f32>(2.0, 2.0, 2.0, 1.0), vec2<u32>(INVALID), INVALID, INVALID, INVALID, vec2<f32>(0.0));
}
@vertex
fn advancedVisibilityVertex(@builtin(vertex_index) vertexIndex: u32) -> VisibilityVertex {
    if (parameters.viewIndex > 255u || parameters.origin > 1u) { return rejectedVertex(); }
    var payloadIndex = parameters.directPayloadIndex;
    var producer = 3u;
    var primitive = INVALID;
    var stream = 0u;
    var vertex = 0u;
    if (parameters.direct == 0u) {
        let triangleIndex = parameters.triangleBase + vertexIndex / 3u;
        if (triangleIndex >= arrayLength(&triangles) / 8u) { return rejectedVertex(); }
        let triangle = triangleIndex * 8u;
        payloadIndex = triangles[triangle];
        primitive = triangles[triangle + 1u];
        producer = triangles[triangle + 2u];
        stream = triangles[triangle + 3u];
        vertex = triangles[triangle + 4u + vertexIndex % 3u];
    }
    if (payloadIndex >= arrayLength(&payloads) / 24u) { return rejectedVertex(); }
    let payload = payloadIndex * 24u;
    if (parameters.direct != 0u) {
        let deformed = (payloads[payload + 22u] & 1u) != 0u;
        producer = select(3u, 4u, deformed);
        stream = select(0u, 2u, deformed);
        let firstIndex = payloads[payload + 15u];
        let index = firstIndex + vertexIndex;
        if (vertexIndex >= payloads[payload + 16u] || index < firstIndex || index >= geometry[5u] / 4u || firstIndex % 3u != 0u) { return rejectedVertex(); }
        let localVertex = geometry[geometry[4u] + index];
        vertex = payloads[payload + 6u] + localVertex;
        if (localVertex >= payloads[payload + 17u] || vertex < localVertex) { return rejectedVertex(); }
        primitive = firstIndex / 3u + vertexIndex / 3u;
    }
    let drawHandle = vec2<u32>(payloads[payload], payloads[payload + 1u]);
    let draw = resolveHandle(0u, drawHandle);
    let mesh = resolveHandle(2u, vec2<u32>(payloads[payload + 2u], payloads[payload + 3u]));
    let material = resolveHandle(3u, vec2<u32>(payloads[payload + 4u], payloads[payload + 5u]));
    if (draw == INVALID || mesh == INVALID || material == INVALID) { return rejectedVertex(); }
    let instance = resolveHandle(1u, loadHandle(draw));
    let transform = resolveHandle(17u, loadHandle(draw + 12u));
    let previousTransform = resolveHandle(17u, loadHandle(draw + 14u));
    if (instance == INVALID || transform == INVALID || previousTransform == INVALID || producer > 4u || primitive == INVALID ||
        (stream != 0u && stream != 2u)) { return rejectedVertex(); }
    let address = vertexAddress(stream, vertex);
    if (address == INVALID) { return rejectedVertex(); }
    let localPosition = positionAt(address);
    let world = loadMatrix(transform) * vec4<f32>(localPosition, 1.0);
    let clip = parameters.viewProjection * world;
    if (!finite4(clip)) { return rejectedVertex(); }
    var previousPosition = localPosition;
    var previousValid = stream == 0u;
    if (stream == 2u && vertex >= payloads[payload + 6u]) {
        let relative = vertex - payloads[payload + 6u];
        let previousVertex = payloads[payload + 7u] + relative;
        let previousAddress = vertexAddress(3u, previousVertex);
        previousValid = previousVertex >= payloads[payload + 7u] && previousAddress != INVALID;
        if (previousValid) { previousPosition = positionAt(previousAddress); }
    }
    let previousWorld = loadMatrix(previousTransform) * vec4<f32>(previousPosition, 1.0);
    let temporalValid = finite4(parameters.viewProjectionUnjittered * world) &&
        finite4(parameters.previousViewProjectionUnjittered * previousWorld);
    let velocityValid = previousValid && (parameters.viewFlags & 128u) != 0u &&
        ((payloads[payload + 22u] >> 16u) & 15u) == 0u && scene[mesh + 5u] != 0u && scene[mesh + 13u] != 0u && temporalValid;
    let editor = resolveHandle(23u, loadHandle(draw + 10u));
    var selection = INVALID;
    var editorFlags = 0u;
    if (editor != INVALID) { selection = scene[editor + 6u]; editorFlags = scene[editor + 7u] & 3u; }
    let metadata = producer | (parameters.origin << 3u) | (parameters.masked << 4u) |
        (select(0u, 1u, velocityValid) << 6u) | (parameters.viewIndex << 8u) | (1u << 16u) |
        (select(0u, 1u, selection != INVALID) << 24u) | (editorFlags << 25u);
    return VisibilityVertex(clip, vec2<u32>(drawHandle.x, primitive), metadata, selection, material, unpack2x16float(geometry[address + 5u]));
}
struct VisibilityOutput {
    @location(0) identity: vec2<u32>,
    @location(1) metadata: u32,
    @location(2) selection: u32,
};
@fragment
fn advancedVisibilityFragment(input: VisibilityVertex, @builtin(front_facing) front: bool) -> VisibilityOutput {
    // Derivatives are evaluated before any material-dependent control flow.
    let dx = dpdx(input.uv);
    let dy = dpdy(input.uv);
    if (input.metadata == INVALID || input.identity.x == 0u || input.identity.x == INVALID || input.identity.y == INVALID) { discard; }
    if (parameters.masked != 0u) {
        let constants = sceneRow(11u, scene[input.material + 11u]);
        if (constants == INVALID || parameters.baseColorWord + 3u >= scene[input.material + 12u] ||
            parameters.alphaCutoffWord >= scene[input.material + 12u] || parameters.materialFlagsWord >= scene[input.material + 12u]) { discard; }
        var alpha = bitcast<f32>(scene[constants + parameters.baseColorWord + 3u]);
        if ((scene[constants + parameters.materialFlagsWord] & 1u) != 0u) {
            alpha *= textureSampleGrad(coverageTexture, coverageSampler, input.uv, dx, dy).a;
        }
        if (alpha < bitcast<f32>(scene[constants + parameters.alphaCutoffWord])) { discard; }
    }
    return VisibilityOutput(input.identity, input.metadata | (select(0u, 1u, front) << 5u), input.selection);
}
