// Engine-owned GPU mip generation. One invocation writes one packed destination
// texel into a copy-compatible staging buffer; no pixel data visits the CPU.
struct Parameters {
    srcMip: u32,
    dstWidth: u32,
    dstHeight: u32,
    srcWidth: u32,
    srcHeight: u32,
    rowWords: u32,
    encoding: u32,
    mode: u32,
}

@group(0) @binding(0) var source: texture_2d<f32>;
@group(0) @binding(1) var<storage, read_write> output: array<u32>;
@group(0) @binding(2) var<uniform> parameters: Parameters;

const lumaWeights = vec3f(0.2126, 0.7152, 0.0722);

fn texel(coord: vec2u) -> vec4f {
    let clamped = min(coord, vec2u(parameters.srcWidth - 1u, parameters.srcHeight - 1u));
    return textureLoad(source, vec2i(clamped), i32(parameters.srcMip));
}

fn detailPreserving(coord: vec2u) -> vec4f {
    let base = coord * 2u;
    let c0 = texel(base);
    let c1 = texel(base + vec2u(1u, 0u));
    let c2 = texel(base + vec2u(0u, 1u));
    let c3 = texel(base + vec2u(1u, 1u));
    let average = (c0 + c1 + c2 + c3) * 0.25;
    let l0 = dot(c0.rgb, lumaWeights);
    let l1 = dot(c1.rgb, lumaWeights);
    let l2 = dot(c2.rgb, lumaWeights);
    let l3 = dot(c3.rgb, lumaWeights);
    let mean = (l0 + l1 + l2 + l3) * 0.25;
    let normalization = max(abs(mean), 0.05);
    let w0 = 1.0 + min(2.0, abs(l0 - mean) / normalization);
    let w1 = 1.0 + min(2.0, abs(l1 - mean) / normalization);
    let w2 = 1.0 + min(2.0, abs(l2 - mean) / normalization);
    let w3 = 1.0 + min(2.0, abs(l3 - mean) / normalization);
    let weighted = (c0 * w0 + c1 * w1 + c2 * w2 + c3 * w3) / max(w0 + w1 + w2 + w3, 1e-5);
    var result = mix(average, weighted, 0.35);
    result.a = average.a;
    let minimum = min(min(c0, c1), min(c2, c3));
    let maximum = max(max(c0, c1), max(c2, c3));
    return clamp(result, minimum, maximum);
}

fn ordinaryBox(coord: vec2u) -> vec4f {
    let first = coord * vec2u(parameters.srcWidth, parameters.srcHeight) /
        vec2u(parameters.dstWidth, parameters.dstHeight);
    let last = (coord + vec2u(1u)) * vec2u(parameters.srcWidth, parameters.srcHeight) /
        vec2u(parameters.dstWidth, parameters.dstHeight);
    var sum = vec4f(0.0);
    var count = 0u;
    for (var y = first.y; y < max(first.y + 1u, last.y); y++) {
        for (var x = first.x; x < max(first.x + 1u, last.x); x++) {
            sum += texel(vec2u(x, y));
            count++;
        }
    }
    return sum / f32(count);
}

fn linearToSrgb(linear: vec3f) -> vec3f {
    let color = clamp(linear, vec3f(0.0), vec3f(1.0));
    let low = color * 12.92;
    let high = vec3f(1.055) * pow(color, vec3f(1.0 / 2.4)) - vec3f(0.055);
    return select(high, low, color <= vec3f(0.0031308));
}

@compute @workgroup_size(16, 16, 1)
fn generate(@builtin(global_invocation_id) id: vec3u) {
    if (id.x >= parameters.dstWidth || id.y >= parameters.dstHeight) { return; }
    var color = ordinaryBox(id.xy);
    if (parameters.mode == 1u) { color = detailPreserving(id.xy); }
    let wordsPerPixel = select(1u, 2u, parameters.encoding == 2u);
    let index = id.y * parameters.rowWords + id.x * wordsPerPixel;
    if (parameters.encoding == 2u) {
        output[index] = pack2x16float(color.rg);
        output[index + 1u] = pack2x16float(color.ba);
    } else {
        if (parameters.encoding == 1u) { color = vec4f(linearToSrgb(color.rgb), color.a); }
        output[index] = pack4x8unorm(color);
    }
}
