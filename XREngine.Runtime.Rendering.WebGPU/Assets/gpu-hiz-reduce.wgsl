// WebGPU lowering of Compute/Occlusion/HiZGen.comp: standard depth uses MAX.
// Texture mips floor their dimensions. A normalized, overlapping footprint
// includes the final row/column at odd sizes instead of dropping edge depths.
@group(0) @binding(0) var sourceDepth: texture_2d<f32>;
@group(0) @binding(1) var destination: texture_storage_2d<r32float, write>;

@compute @workgroup_size(8, 8)
fn reduceDepth(@builtin(global_invocation_id) invocation: vec3<u32>) {
    let coordinate = invocation.xy;
    let destinationSize = textureDimensions(destination);
    if (any(coordinate >= destinationSize)) { return; }
    let sourceSize = textureDimensions(sourceDepth, 0);
    // For floor-sized mips the normalized source interval is [2*x,2*x+2)
    // for even dimensions and [2*x,2*x+3) for odd dimensions. This form
    // avoids multiplying two potentially large texture dimensions.
    let first = coordinate * vec2<u32>(2u);
    let end = min(first + vec2<u32>(2u) + sourceSize % vec2<u32>(2u), sourceSize);
    var depth = 0.0;
    for (var y = first.y; y < end.y; y++) {
        for (var x = first.x; x < end.x; x++) {
            let sampleDepth = textureLoad(sourceDepth, vec2<i32>(i32(x), i32(y)), 0).r;
            depth = max(depth, select(1.0, sampleDepth, sampleDepth >= 0.0 && sampleDepth <= 1.0));
        }
    }
    textureStore(destination, vec2<i32>(coordinate), vec4<f32>(depth, 0.0, 0.0, 0.0));
}
