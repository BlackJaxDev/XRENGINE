// Engine-owned 2D reduction. Single-layer textures keep their native 2d binding
// dimension, including on WebGPU compatibility devices.
struct Parameters {
    origin: vec2<u32>,
    extent: vec2<u32>,
    mip: u32,
    layers: u32,
    tilesX: u32,
    tilesY: u32,
    tileCount: u32,
    mode: u32,
    weights: vec4f,
}

@group(0) @binding(0) var source: texture_2d<f32>;
@group(0) @binding(1) var<storage, read_write> partials: array<vec2f>;
@group(0) @binding(2) var<storage, read_write> result: array<f32>;
@group(0) @binding(3) var<uniform> parameters: Parameters;

var<workgroup> sums: array<vec2f, 256>;

@compute @workgroup_size(256, 1, 1)
fn reduce(@builtin(local_invocation_index) lane: u32,
          @builtin(workgroup_id) group: vec3u) {
    var value = vec2f(0.0);
    if (parameters.mode == 0u) {
        let tile = group.x;
        let x = (tile % parameters.tilesX) * 16u + lane % 16u;
        let y = (tile / parameters.tilesX) * 16u + lane / 16u;
        if (x < parameters.extent.x && y < parameters.extent.y) {
            let color = textureLoad(source, vec2i(parameters.origin + vec2u(x, y)), i32(parameters.mip));
            let invalid = any((bitcast<vec3<u32>>(color.rgb) & vec3<u32>(0x7f800000u)) == vec3<u32>(0x7f800000u));
            let brightness = dot(color.rgb, parameters.weights.rgb);
            value = vec2f(select(brightness, 0.0, invalid), select(0.0, 1.0, invalid));
        }
    } else {
        for (var index = lane; index < parameters.tileCount; index += 256u) {
            value += partials[index];
        }
    }
    sums[lane] = value;
    workgroupBarrier();
    for (var stride = 128u; stride > 0u; stride >>= 1u) {
        if (lane < stride) { sums[lane] += sums[lane + stride]; }
        workgroupBarrier();
    }
    if (lane == 0u) {
        if (parameters.mode == 0u) {
            partials[group.x] = sums[0];
        } else {
            let count = f32(parameters.extent.x) * f32(parameters.extent.y);
            let average = sums[0].x / count;
            result[0] = select(average, bitcast<f32>(0x7fc00000u), sums[0].y > 0.0);
        }
    }
}
