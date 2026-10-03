// Canonical MeshletPayloadV3 lowering. Source-primitive destinations preserve
// both authored triangle order and fragment primitive_index after meshlet cooking.
struct CullParameters {
    modelMatrix: mat4x4<f32>,
    viewProjection: mat4x4<f32>,
    meshletCount: u32,
    sourceTriangleCount: u32,
    vertexCount: u32,
    indexCapacity: u32,
    descriptorWordOffset: u32,
    remapWordOffset: u32,
    remapCount: u32,
    triangleWordOffset: u32,
    triangleByteCount: u32,
    primitiveWordOffset: u32,
    primitiveCount: u32,
    boundsWordOffset: u32,
    cullEnabled: u32,
    useRefitBounds: u32,
    sphereExpansion: f32,
    drawEnabled: u32,
};
@group(0) @binding(0) var<storage, read> source: array<u32>;
@group(0) @binding(1) var<storage, read> bounds: array<u32>;
@group(0) @binding(2) var<storage, read_write> indices: array<u32>;
// Five drawIndexedIndirect words, fault, completed meshlets, visible triangles.
@group(0) @binding(3) var<storage, read_write> state: array<atomic<u32>>;
@group(0) @binding(4) var<uniform> parameters: CullParameters;
var<workgroup> selected: array<u32, 8>;

fn finite(value: f32) -> bool {
    return (bitcast<u32>(value) & 0x7f800000u) != 0x7f800000u;
}
fn finite4(value: vec4<f32>) -> bool {
    return all((bitcast<vec4<u32>>(value) & vec4<u32>(0x7f800000u)) != vec4<u32>(0x7f800000u));
}
fn sourceRange(offset: u32, count: u32) -> bool {
    let length = arrayLength(&source);
    return offset <= length && count <= length - min(offset, length);
}
fn fault(value: u32) {
    if (arrayLength(&state) >= 8u) { atomicOr(&state[5u], value); }
}
fn outsidePlane(sphere: vec4<f32>, plane: vec4<f32>) -> bool {
    // Normalize by the largest coefficient before evaluating, avoiding overflow
    // for extreme projections. Invalid arithmetic can only disable culling.
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
fn visibleSphere(sphere: vec4<f32>) -> bool {
    if (parameters.cullEnabled == 0u || !finite4(sphere) || sphere.w < 0.0 ||
        !finite(parameters.sphereExpansion) || parameters.sphereExpansion < 0.0) { return true; }
    let expanded = vec4<f32>(sphere.xyz, sphere.w + parameters.sphereExpansion);
    if (!finite4(expanded)) { return true; }
    // Object-space planes make this conservative for arbitrary affine model
    // transforms, including shear, reflection, and non-uniform scale.
    let clip = parameters.viewProjection * parameters.modelMatrix;
    if (!finite4(clip[0]) || !finite4(clip[1]) || !finite4(clip[2]) || !finite4(clip[3])) { return true; }
    let rows = transpose(clip);
    return !outsidePlane(expanded, rows[3] + rows[0]) && !outsidePlane(expanded, rows[3] - rows[0]) &&
        !outsidePlane(expanded, rows[3] + rows[1]) && !outsidePlane(expanded, rows[3] - rows[1]) &&
        !outsidePlane(expanded, rows[2]) && !outsidePlane(expanded, rows[3] - rows[2]);
}
fn prepareMeshlet(meshlet: u32) {
    selected[0] = 0u;
    if (meshlet >= parameters.meshletCount || arrayLength(&state) < 8u) { return; }
    atomicAdd(&state[6u], 1u);
    if (parameters.sourceTriangleCount > 0x3fffffffu ||
        parameters.sourceTriangleCount * 3u > parameters.indexCapacity ||
        parameters.indexCapacity > arrayLength(&indices)) { fault(1u); return; }
    if (parameters.vertexCount == 0u || !sourceRange(parameters.descriptorWordOffset, 0u) ||
        meshlet >= (arrayLength(&source) - min(parameters.descriptorWordOffset, arrayLength(&source))) / 20u ||
        !sourceRange(parameters.remapWordOffset, parameters.remapCount) ||
        !sourceRange(parameters.primitiveWordOffset, parameters.primitiveCount) ||
        meshlet >= parameters.primitiveCount / 124u ||
        !sourceRange(parameters.triangleWordOffset, parameters.triangleByteCount / 4u + select(0u, 1u, parameters.triangleByteCount % 4u != 0u))) {
        fault(2u); return;
    }
    let descriptor = parameters.descriptorWordOffset + meshlet * 20u;
    let vertexOffset = source[descriptor + 4u];
    let triangleOffset = source[descriptor + 5u];
    let vertexCount = source[descriptor + 6u];
    let triangleCount = source[descriptor + 7u];
    if (vertexCount == 0u || vertexCount > 64u || triangleCount == 0u || triangleCount > 124u ||
        vertexOffset > parameters.remapCount || vertexCount > parameters.remapCount - min(vertexOffset, parameters.remapCount) ||
        triangleOffset % 4u != 0u || triangleOffset > parameters.triangleByteCount ||
        triangleCount > (parameters.triangleByteCount - min(triangleOffset, parameters.triangleByteCount)) / 3u) {
        fault(2u); return;
    }
    var sphere = bitcast<vec4<f32>>(vec4<u32>(source[descriptor], source[descriptor + 1u], source[descriptor + 2u], source[descriptor + 3u]));
    if (parameters.useRefitBounds != 0u) {
        // A missing or invalid envelope is unbounded, never an invisible meshlet.
        sphere = vec4<f32>(0.0, 0.0, 0.0, -1.0);
        if (parameters.boundsWordOffset <= arrayLength(&bounds) &&
            meshlet < (arrayLength(&bounds) - min(parameters.boundsWordOffset, arrayLength(&bounds))) / 4u) {
            let offset = parameters.boundsWordOffset + meshlet * 4u;
            sphere = bitcast<vec4<f32>>(vec4<u32>(bounds[offset], bounds[offset + 1u], bounds[offset + 2u], bounds[offset + 3u]));
        }
    }
    // Cone culling remains disabled: raster state and authored deformation do
    // not establish a current conservative cone merely by supplying old data.
    selected[0] = select(1u, 2u, parameters.drawEnabled != 0u && visibleSphere(sphere));
    selected[1] = parameters.remapWordOffset + vertexOffset;
    selected[2] = vertexCount;
    selected[3] = triangleOffset;
    selected[4] = triangleCount;
    selected[5] = parameters.primitiveWordOffset + meshlet * 124u;
}
fn triangleByte(offset: u32) -> u32 {
    let word = source[parameters.triangleWordOffset + offset / 4u];
    return (word >> ((offset % 4u) * 8u)) & 255u;
}
@compute @workgroup_size(64, 1, 1)
fn meshletsCullExpand(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) local: u32) {
    let meshlet = group.x + group.y * 65535u;
    if (local == 0u) { prepareMeshlet(meshlet); }
    workgroupBarrier();
    if (selected[0] == 0u) { return; }
    for (var triangle = local; triangle < selected[4]; triangle += 64u) {
        // Low 30 bits identify the authored primitive; high two bits restore
        // its cyclic corner rotation without changing winding or barycentrics.
        let mapping = source[selected[5] + triangle];
        let primitive = mapping & 0x3fffffffu;
        let rotation = mapping >> 30u;
        if (primitive >= parameters.sourceTriangleCount || rotation > 2u) { fault(2u); continue; }
        let byte = selected[3] + triangle * 3u;
        let localVertices = vec3<u32>(triangleByte(byte + rotation), triangleByte(byte + (rotation + 1u) % 3u), triangleByte(byte + (rotation + 2u) % 3u));
        if (any(localVertices >= vec3<u32>(selected[2]))) { fault(2u); continue; }
        let vertices = vec3<u32>(source[selected[1] + localVertices.x], source[selected[1] + localVertices.y], source[selected[1] + localVertices.z]);
        if (any(vertices >= vec3<u32>(parameters.vertexCount))) { fault(2u); continue; }
        let destination = primitive * 3u;
        let emitted = select(vec3<u32>(0u), vertices, selected[0] == 2u);
        indices[destination] = emitted.x;
        indices[destination + 1u] = emitted.y;
        indices[destination + 2u] = emitted.z;
        if (selected[0] == 2u) { atomicAdd(&state[7u], 1u); }
    }
}
