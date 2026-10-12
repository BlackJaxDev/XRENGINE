// Publish one original indexed draw without rewriting its authored geometry.
struct CullParameters {
    modelMatrix: mat4x4<f32>,
    viewProjection: mat4x4<f32>,
    indexCount: u32,
    candidateMeshId: u32,
    candidateLod: u32,
    drawEnabled: u32,
    cullEnabled: u32,
    sphereExpansion: f32,
    vertexCount: u32,
    positionWordCount: u32,
    positionStrideWords: u32,
    positionOffsetWords: u32,
    instanceCount: u32,
    instanceSourceEnabled: u32,
    instanceStrideWords: u32,
    instanceTransformOffsetWords: u32,
    instanceBoundsOffsetWords: u32,
    instanceWordCount: u32,
    reserved0: u32,
    reserved1: u32,
    reserved2: u32,
    reserved3: u32,
};
@group(0) @binding(0) var<storage, read> selectedLod: array<u32>;
// Five drawIndexedIndirect words: count, instances, firstIndex, baseVertex, firstInstance.
@group(0) @binding(1) var<storage, read_write> arguments: array<u32>;
@group(0) @binding(2) var<storage, read> positions: array<u32>;
@group(0) @binding(3) var<storage, read> instances: array<u32>;
@group(0) @binding(4) var<uniform> parameters: CullParameters;
var<workgroup> lower: array<vec3<f32>, 64>;
var<workgroup> upper: array<vec3<f32>, 64>;
var<workgroup> invalid: array<u32, 64>;

fn finite(value: f32) -> bool {
    return (bitcast<u32>(value) & 0x7f800000u) != 0x7f800000u;
}
fn finite3(value: vec3<f32>) -> bool {
    return all((bitcast<vec3<u32>>(value) & vec3<u32>(0x7f800000u)) != vec3<u32>(0x7f800000u));
}
fn finite4(value: vec4<f32>) -> bool {
    return all((bitcast<vec4<u32>>(value) & vec4<u32>(0x7f800000u)) != vec4<u32>(0x7f800000u));
}
fn outsidePlane(sphere: vec4<f32>, plane: vec4<f32>) -> bool {
    // Scale first to avoid projection overflow. Uncertain arithmetic stays visible.
    if (!finite4(plane)) { return false; }
    let scale = max(max(abs(plane.x), abs(plane.y)), max(abs(plane.z), abs(plane.w)));
    if (!(scale > 0.0)) { return false; }
    let normalized = plane / scale;
    let normalLength = length(normalized.xyz);
    let distance = dot(normalized.xyz, sphere.xyz) + normalized.w;
    let radius = sphere.w * normalLength;
    let terms = abs(normalized.xyz * sphere.xyz);
    let tolerance = (terms.x + terms.y + terms.z + abs(normalized.w) + radius + 1.0) * 0.00001;
    return finite(normalLength) && normalLength > 0.0 && finite(distance) && finite(radius) &&
        finite(tolerance) && distance < -radius - tolerance;
}
fn visibleTransformedSphere(sphere: vec4<f32>, model: mat4x4<f32>) -> bool {
    if (parameters.cullEnabled == 0u || !finite4(sphere) || sphere.w < 0.0 ||
        !finite(parameters.sphereExpansion) || parameters.sphereExpansion < 0.0) { return true; }
    let expanded = vec4<f32>(sphere.xyz, sphere.w + parameters.sphereExpansion);
    if (!finite4(expanded)) { return true; }
    // Pull clip planes into object space so shear, reflection and nonuniform
    // affine scale do not require an approximate world-space sphere radius.
    let clip = parameters.viewProjection * model;
    if (!finite4(clip[0]) || !finite4(clip[1]) || !finite4(clip[2]) || !finite4(clip[3])) { return true; }
    let rows = transpose(clip);
    // The original Vulkan-style view projection and WebGPU share [0, w] depth.
    // Flipping clip Y for rasterization exchanges the same two Y planes.
    return !outsidePlane(expanded, rows[3] + rows[0]) && !outsidePlane(expanded, rows[3] - rows[0]) &&
        !outsidePlane(expanded, rows[3] + rows[1]) && !outsidePlane(expanded, rows[3] - rows[1]) &&
        !outsidePlane(expanded, rows[2]) && !outsidePlane(expanded, rows[3] - rows[2]);
}

fn instanceWord(offset: u32) -> f32 { return bitcast<f32>(instances[offset]); }
fn instanceVector(offset: u32) -> vec4<f32> {
    return vec4<f32>(instanceWord(offset), instanceWord(offset + 1u), instanceWord(offset + 2u), instanceWord(offset + 3u));
}
fn visibleSphere(sphere: vec4<f32>) -> bool {
    if (parameters.cullEnabled == 0u) { return true; }
    if (parameters.instanceSourceEnabled == 0u) { return visibleTransformedSphere(sphere, parameters.modelMatrix); }
    // Each row is the same authored native instance_index row used by raster.
    // Retain the entire draw when any instance is visible, preserving order and
    // firstInstance=0 without optional indirect-first-instance or compaction.
    if (parameters.instanceStrideWords < 36u || parameters.instanceWordCount > arrayLength(&instances) ||
        parameters.instanceCount > parameters.instanceWordCount / parameters.instanceStrideWords ||
        parameters.instanceTransformOffsetWords > parameters.instanceStrideWords - 16u ||
        parameters.instanceBoundsOffsetWords > parameters.instanceStrideWords - 4u) { return true; }
    for (var instance = 0u; instance < parameters.instanceCount; instance++) {
        let row = instance * parameters.instanceStrideWords;
        let offset = row + parameters.instanceTransformOffsetWords;
        let transform = mat4x4<f32>(instanceVector(offset), instanceVector(offset + 4u),
                                   instanceVector(offset + 8u), instanceVector(offset + 12u));
        // This declared full-instance envelope remains valid for unknown vertex
        // behavior. It never invents a finer bound from undeformed mesh data.
        let bound = instanceVector(row + parameters.instanceBoundsOffsetWords);
        if (visibleTransformedSphere(bound, parameters.modelMatrix * transform)) { return true; }
    }
    return false;
}

fn validPositionRange() -> bool {
    // Unknown or oversized streams disable rejection, never truncate the bound.
    if (parameters.vertexCount == 0u || parameters.vertexCount > 1048576u ||
        parameters.positionStrideWords < 3u || parameters.positionOffsetWords > parameters.positionStrideWords - 3u ||
        parameters.positionWordCount > arrayLength(&positions) || parameters.positionOffsetWords > parameters.positionWordCount ||
        parameters.positionWordCount - parameters.positionOffsetWords < 3u) { return false; }
    // Division proves the last complete xyz load is in range without overflow.
    return parameters.vertexCount - 1u <=
        (parameters.positionWordCount - parameters.positionOffsetWords - 3u) / parameters.positionStrideWords;
}
@compute @workgroup_size(64, 1, 1)
fn indirectCullPrimitive(@builtin(local_invocation_index) local: u32) {
    let lodSelected = arrayLength(&selectedLod) >= 2u && parameters.candidateMeshId != 0u &&
        selectedLod[0u] == parameters.candidateMeshId && selectedLod[1u] == parameters.candidateLod;
    let scan = parameters.drawEnabled != 0u && lodSelected && parameters.cullEnabled != 0u && validPositionRange();
    var localLower = vec3<f32>(3.402823466e+38);
    var localUpper = vec3<f32>(-3.402823466e+38);
    var localInvalid = select(1u, 0u, scan);
    if (scan) {
        // Include every vertex, even unused vertices, from the exact raster stream.
        for (var vertex = local; vertex < parameters.vertexCount; vertex += 64u) {
            let offset = vertex * parameters.positionStrideWords + parameters.positionOffsetWords;
            let position = bitcast<vec3<f32>>(vec3<u32>(positions[offset], positions[offset + 1u], positions[offset + 2u]));
            if (finite3(position)) {
                localLower = min(localLower, position);
                localUpper = max(localUpper, position);
            } else { localInvalid = 1u; }
        }
    }
    lower[local] = localLower;
    upper[local] = localUpper;
    invalid[local] = localInvalid;
    // Every lane reaches every barrier, including disabled and invalid draws.
    workgroupBarrier();
    for (var stride = 32u; stride > 0u; stride /= 2u) {
        if (local < stride) {
            lower[local] = min(lower[local], lower[local + stride]);
            upper[local] = max(upper[local], upper[local + stride]);
            invalid[local] |= invalid[local + stride];
        }
        workgroupBarrier();
    }
    if (local == 0u && arrayLength(&arguments) >= 5u) {
        var sphere = vec4<f32>(0.0, 0.0, 0.0, -1.0);
        let center = lower[0u] * 0.5 + upper[0u] * 0.5;
        let extent = max(abs(upper[0u] - center), abs(center - lower[0u]));
        let radius = length(extent) * 1.00001;
        if (invalid[0u] == 0u && finite3(center) && finite3(extent) && finite(radius)) {
            sphere = vec4<f32>(center, radius);
        }
        let accepted = parameters.drawEnabled != 0u && lodSelected && visibleSphere(sphere);
        // Always replace the complete record, including when its previous draw was visible.
        arguments[0u] = select(0u, parameters.indexCount, accepted);
        arguments[1u] = select(0u, parameters.instanceCount, accepted);
        arguments[2u] = 0u;
        arguments[3u] = 0u;
        arguments[4u] = 0u;
    }
}
