// Backend lowering of Compute/Animation/SkinningPrepass.comp. Canonical packed
// records are concatenated into one binding to fit mobile storage-binding limits.
struct Update { activeCount: u32, reserved0: u32, reserved1: u32, reserved2: u32 }
@group(0) @binding(0) var<storage, read> data: array<u32>;
@group(0) @binding(1) var<storage, read> palette: array<vec4f>;
@group(0) @binding(2) var<storage, read> activeMorphs: array<vec2f>;
@group(0) @binding(3) var<storage, read_write> vertices: array<f32>;
@group(0) @binding(4) var<storage, read_write> attributes: array<vec4f>;
@group(0) @binding(5) var<uniform> update: Update;

struct Deformed { position: vec3f, normal: vec3f, tangent: vec3f }
struct Weighted { value: Deformed, total: f32 }

fn f(index: u32) -> f32 { return bitcast<f32>(data[index]); }
fn v3(index: u32) -> vec3f { return vec3f(f(index), f(index + 1u), f(index + 2u)); }
fn unitOrZero(value: vec3f) -> vec3f {
    let lengthSquared = dot(value, value);
    if (lengthSquared <= 1e-20) { return vec3f(0.0); }
    return value * inverseSqrt(lengthSquared);
}

fn delta(index: u32, shape: u32) -> vec3f {
    if (index == 0u) { return vec3f(0.0); }
    let base = data[17] + index * 2u;
    let xy = unpack2x16snorm(data[base]);
    let z = unpack2x16snorm(data[base + 1u]).x;
    let metadata = data[18] + shape * 16u;
    return v3(metadata + 12u) + vec3f(xy, z) * v3(metadata + 8u);
}

fn morph(source: Deformed, vertex: u32) -> Deformed {
    var result = source;
    var accumulated = Deformed(vec3f(0.0), vec3f(0.0), vec3f(0.0));
    for (var activeIndex = 0u; activeIndex < update.activeCount; activeIndex++) {
        let record = activeMorphs[activeIndex];
        if (abs(record.y) <= f(24u)) { continue; }
        let shape = u32(record.x);
        let range = data[15] + shape * 4u;
        var low = data[range];
        var high = low + data[range + 1u];
        var found = 0xffffffffu;
        while (low < high) {
            let mid = low + (high - low) / 2u;
            let candidate = data[data[16] + mid * 4u];
            if (candidate < vertex) { low = mid + 1u; }
            else if (candidate > vertex) { high = mid; }
            else { found = mid; break; }
        }
        if (found == 0xffffffffu) { continue; }
        let offset = data[16] + found * 4u;
        let p = delta(data[offset + 1u], shape) * record.y;
        let n = delta(data[offset + 2u], shape) * record.y;
        let t = delta(data[offset + 3u], shape) * record.y;
        if ((data[5] & 8u) != 0u) {
            accumulated.position = max(accumulated.position, p);
            accumulated.normal = max(accumulated.normal, n);
            accumulated.tangent = max(accumulated.tangent, t);
        } else {
            accumulated.position += p;
            accumulated.normal += n;
            accumulated.tangent += t;
        }
    }
    result.position += accumulated.position;
    if ((data[5] & 1u) != 0u) { result.normal += accumulated.normal; }
    if ((data[5] & 2u) != 0u) { result.tangent += accumulated.tangent; }
    return result;
}

fn addInfluence(current: Weighted, source: Deformed, bone: u32, weight: f32) -> Weighted {
    if (weight <= 0.0 || bone >= data[3]) { return current; }
    var result = current;
    let a = palette[bone * 3u];
    let b = palette[bone * 3u + 1u];
    let c = palette[bone * 3u + 2u];
    let p = vec4f(source.position, 1.0);
    result.value.position += vec3f(dot(a, p), dot(b, p), dot(c, p)) * weight;
    let ca = cross(b.xyz, c.xyz);
    let cb = cross(c.xyz, a.xyz);
    let cc = cross(a.xyz, b.xyz);
    result.value.normal += vec3f(dot(ca, source.normal), dot(cb, source.normal), dot(cc, source.normal)) * weight;
    result.value.tangent += vec3f(dot(ca, source.tangent), dot(cb, source.tangent), dot(cc, source.tangent)) * weight;
    result.total += weight;
    return result;
}

@compute @workgroup_size(64)
fn skin(@builtin(global_invocation_id) id: vec3u) {
    let vertex = id.x;
    if (vertex >= data[2]) { return; }
    let sourceOffset = data[8] + vertex * 5u;
    var source = Deformed(v3(sourceOffset), vec3f(0.0), vec3f(0.0));
    var handedness = 1.0;
    if ((data[5] & 1u) != 0u) { source.normal = v3(data[9] + vertex * 3u); }
    if ((data[5] & 2u) != 0u) {
        source.tangent = v3(data[10] + vertex * 4u);
        handedness = f(data[10] + vertex * 4u + 3u);
    }
    source = morph(source, vertex);
    var result = source;
    if ((data[5] & 4u) != 0u) {
        var weighted = Weighted(Deformed(vec3f(0.0), vec3f(0.0), vec3f(0.0)), 0.0);
        let weights = unpack4x8unorm(data[data[12] + vertex]);
        for (var lane = 0u; lane < min(4u, data[19]); lane++) {
            var bone: u32;
            if (data[4] == 1u) { bone = (data[data[11] + vertex] >> (lane * 8u)) & 255u; }
            else { bone = (data[data[11] + vertex * 2u + lane / 2u] >> ((lane & 1u) * 16u)) & 65535u; }
            weighted = addInfluence(weighted, source, bone, weights[lane]);
        }
        if ((data[5] & 16u) != 0u) {
            let header = data[data[13] + vertex];
            let count = header >> 24u;
            let offset = header & 0xffffffu;
            for (var lane = 0u; lane < count && lane + 4u < data[19]; lane++) {
                let entry = data[data[14] + offset + lane];
                weighted = addInfluence(weighted, source, entry & 65535u, f32((entry >> 16u) & 255u) / 255.0);
            }
        }
        if (weighted.total > 0.0001) { result = weighted.value; }
    }
    let destination = vertex * 5u;
    vertices[destination] = result.position.x;
    vertices[destination + 1u] = result.position.y;
    vertices[destination + 2u] = result.position.z;
    vertices[destination + 3u] = f(sourceOffset + 3u);
    vertices[destination + 4u] = f(sourceOffset + 4u);
    attributes[vertex * 2u] = vec4f(unitOrZero(result.normal), 0.0);
    attributes[vertex * 2u + 1u] = vec4f(unitOrZero(result.tangent), handedness);
}
