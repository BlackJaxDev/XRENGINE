// WebGPU lowering of Compute/Occlusion/GPURenderHiZInit.comp.
// This profile uses zero-to-one standard depth: clear/far = 1.
@group(0) @binding(0) var sourceDepth: texture_depth_2d;
@group(0) @binding(1) var destination: texture_storage_2d<r32float, write>;

@compute @workgroup_size(8, 8)
fn initializeDepth(@builtin(global_invocation_id) invocation: vec3<u32>) {
    let coordinate = invocation.xy;
    if (any(coordinate >= textureDimensions(destination))) { return; }
    let depth = textureLoad(sourceDepth, vec2<i32>(coordinate), 0);
    // Invalid source data cannot establish occlusion.
    let conservative = select(1.0, depth, depth >= 0.0 && depth <= 1.0);
    textureStore(destination, vec2<i32>(coordinate), vec4<f32>(conservative, 0.0, 0.0, 0.0));
}
