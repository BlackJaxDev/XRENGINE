// Resolve one covered surface, preserving the same sample's depth and encoded normal.
// Raw multisample depth remains attached to subsequent forward raster passes.
struct ResolveUniforms { parameters: vec4<f32> };
@group(0) @binding(0) var<uniform> resolve: ResolveUniforms;
@group(1) @binding(0) var multisampleDepth: texture_depth_multisampled_2d;
@group(1) @binding(1) var multisampleNormal: texture_multisampled_2d<f32>;

struct ResolvedSurface {
    @location(0) normal: vec4<f32>,
    @builtin(frag_depth) depth: f32,
};

@vertex
fn resolveVertex(@location(0) position: vec3<f32>) -> @builtin(position) vec4<f32> {
    return vec4<f32>(position.xy, 0.0, 1.0);
}

@fragment
fn resolveFragment(@builtin(position) position: vec4<f32>) -> ResolvedSurface {
    let pixel = vec2<i32>(position.xy);
    if (any(pixel < vec2<i32>(0)) || any(pixel >= vec2<i32>(resolve.parameters.xy))) { discard; }
    let reversed = resolve.parameters.z != 0.0;
    let farDepth = select(1.0, 0.0, reversed);
    var result = ResolvedSurface(vec4<f32>(0.0), farDepth);
    var found = false;
    for (var sampleIndex = 0; sampleIndex < 4; sampleIndex++) {
        let normal = textureLoad(multisampleNormal, pixel, sampleIndex);
        let depth = textureLoad(multisampleDepth, pixel, sampleIndex);
        // All admitted depth/normal companions write alpha one; clear samples have alpha zero.
        if (normal.w <= 0.0 || (bitcast<u32>(depth) & 0x7f800000u) == 0x7f800000u ||
            depth < 0.0 || depth > 1.0) { continue; }
        let closer = select(depth < result.depth, depth > result.depth, reversed);
        if (!found || closer) {
            found = true;
            result = ResolvedSurface(normal, depth);
        }
    }
    return result;
}
