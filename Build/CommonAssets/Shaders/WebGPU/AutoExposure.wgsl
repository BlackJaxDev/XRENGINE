// Mipless exposure metering matches Vulkan's render-graph-owned HDR path.
// History is storage-buffer state; the R32F output is write-only and may be
// sampled by subsequent passes without optional read-write storage textures.
struct ExposureParameters {
    luminanceWeights: vec3<f32>, bias: f32,
    scale: f32, dividend: f32, minimum: f32, maximum: f32,
    exposureBase: f32, fallback: f32, alpha: f32, mode: u32,
    targetSize: u32, ignoreTopPercent: f32, centerStrength: f32, centerPower: f32,
};
@group(0) @binding(0) var sourceTexture: texture_2d<f32>;
@group(0) @binding(1) var exposureOutput: texture_storage_2d<r32float, write>;
@group(0) @binding(2) var<storage, read_write> history: array<f32>;
@group(0) @binding(3) var<uniform> parameters: ExposureParameters;
var<workgroup> luminances: array<f32, 256>;
var<workgroup> weights: array<f32, 256>;

fn finite(value: f32) -> bool { return (bitcast<u32>(value) & 0x7f800000u) != 0x7f800000u; }
fn safeLuminance(rgb: vec3<f32>) -> f32 {
    let nonnegative = max(rgb, vec3<f32>(0.0));
    if (!(finite(nonnegative.x) && finite(nonnegative.y) && finite(nonnegative.z))) { return 0.0; }
    return max(dot(nonnegative, parameters.luminanceWeights), 0.0);
}
fn aspectGrid(size: vec2<u32>) -> vec2<u32> {
    let target = clamp(parameters.targetSize, 1u, 16u);
    if (size.x >= size.y) {
        let x = min(size.x, target);
        return vec2<u32>(x, max(1u, min(size.y, (size.y * x + max(size.x / 2u, 1u)) / size.x)));
    }
    let y = min(size.y, target);
    return vec2<u32>(max(1u, min(size.x, (size.x * y + max(size.y / 2u, 1u)) / size.y)), y);
}
fn sampleGrid(local: u32, grid: vec2<u32>, size: vec2<u32>) -> f32 {
    let cell = vec2<u32>(local % grid.x, local / grid.x);
    let first = cell * size / grid;
    let last = max(first + vec2<u32>(1u), (cell + vec2<u32>(1u)) * size / grid);
    let block = max(last - first, vec2<u32>(1u));
    var total = 0.0;
    for (var y = 0u; y < 4u; y++) {
        for (var x = 0u; x < 4u; x++) {
            let pixel = min(first + ((vec2<u32>(x, y) * 2u + vec2<u32>(1u)) * block) / 8u, size - vec2<u32>(1u));
            total += safeLuminance(textureLoad(sourceTexture, vec2<i32>(pixel), 0).rgb);
        }
    }
    return total / 16.0;
}
fn centerWeight(local: u32, grid: vec2<u32>) -> f32 {
    let uv = (vec2<f32>(f32(local % grid.x), f32(local / grid.x)) + vec2<f32>(0.5)) / vec2<f32>(grid);
    let radius = length(uv - vec2<f32>(0.5)) / 0.70710678;
    let center = pow(clamp(1.0 - radius, 0.0, 1.0), max(parameters.centerPower, 0.1));
    return mix(1.0, center, clamp(parameters.centerStrength, 0.0, 1.0));
}
fn reduceSum(local: u32) -> f32 {
    for (var stride = 128u; stride > 0u; stride >>= 1u) {
        if (local < stride) { luminances[local] += luminances[local + stride]; }
        workgroupBarrier();
    }
    let total = luminances[0];
    workgroupBarrier();
    return total;
}
fn reduceWeighted(local: u32) -> f32 {
    for (var stride = 128u; stride > 0u; stride >>= 1u) {
        if (local < stride) {
            luminances[local] += luminances[local + stride];
            weights[local] += weights[local + stride];
        }
        workgroupBarrier();
    }
    let average = luminances[0] / max(weights[0], 1e-6);
    workgroupBarrier();
    return average;
}
fn sortShared(local: u32) {
    for (var width = 2u; width <= 256u; width <<= 1u) {
        for (var stride = width >> 1u; stride > 0u; stride >>= 1u) {
            let partner = local ^ stride;
            if (partner > local) {
                let ascending = (local & width) == 0u;
                let a = luminances[local];
                let b = luminances[partner];
                if ((a > b) == ascending) {
                    luminances[local] = b;
                    luminances[partner] = a;
                }
            }
            workgroupBarrier();
        }
    }
}
fn meteredLuminance(local: u32) -> f32 {
    let size = max(textureDimensions(sourceTexture, 0), vec2<u32>(1u));
    let grid = aspectGrid(size);
    let count = clamp(grid.x * grid.y, 1u, 256u);
    var luminance = 0.0;
    if (local < count) { luminance = sampleGrid(local, grid, size); }
    if (parameters.mode == 2u) {
        var weight = 0.0;
        if (local < count) { weight = centerWeight(local, grid); }
        luminances[local] = luminance * weight;
        weights[local] = weight;
        workgroupBarrier();
        return reduceWeighted(local);
    }
    // Vulkan's filtered mipless LogAverage explicitly uses the arithmetic
    // mean of the spatially filtered cells, avoiding a second dark-scene bias.
    let drop = select(0.0, clamp(parameters.ignoreTopPercent, 0.0, 0.5), parameters.mode == 3u);
    if (drop > 0.0) {
        luminances[local] = select(3.402823e38, luminance, local < count);
        workgroupBarrier();
        sortShared(local);
        let keep = clamp(u32(floor((1.0 - drop) * f32(count))), 1u, count);
        if (local >= keep) { luminances[local] = 0.0; }
        workgroupBarrier();
        return reduceSum(local) / f32(keep);
    }
    luminances[local] = luminance;
    workgroupBarrier();
    return reduceSum(local) / f32(count);
}
@compute @workgroup_size(256, 1, 1)
fn autoExposure(@builtin(local_invocation_index) local: u32) {
    var luminance = meteredLuminance(local);
    if (local != 0u) { return; }
    let denominator = max(parameters.minimum / max(parameters.exposureBase, 1e-6) - parameters.bias, 1e-6);
    luminance = min(luminance, parameters.dividend * max(parameters.scale, 0.0) / denominator);
    let current = history[0];
    let initialized = history[1] == 1.0;
    let currentValid = initialized && finite(current) && current >= parameters.minimum && current <= parameters.maximum;
    var exposure = clamp(parameters.fallback, parameters.minimum, parameters.maximum);
    if (currentValid) { exposure = current; }
    if (luminance > 0.0 && finite(luminance)) {
        let target = clamp((parameters.bias + parameters.scale * parameters.dividend / luminance) * parameters.exposureBase,
            parameters.minimum, parameters.maximum);
        if (finite(target)) {
            // Aborted frame recording cannot consume the first GPU history step.
            let alpha = select(0.0, clamp(parameters.alpha, 0.0, 1.0), initialized);
            exposure = mix(exposure, target, alpha);
        }
    }
    history[0] = exposure;
    history[1] = 1.0;
    textureStore(exposureOutput, vec2<i32>(0), vec4<f32>(exposure, 0.0, 0.0, 0.0));
}
