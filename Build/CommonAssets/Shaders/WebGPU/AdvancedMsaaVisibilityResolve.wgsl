// Canonical tuple/depth resolve from the exact two-target packed16 visibility ABI.
struct ResolveParameters { screenExtent: vec2<u32>, reversedDepth: u32, reserved: u32 };
@group(0) @binding(0) var rawVisibilityIdentity: texture_multisampled_2d<u32>;
@group(0) @binding(1) var rawVisibilityMetadataSelection: texture_multisampled_2d<u32>;
@group(0) @binding(2) var rawVisibilityDepth: texture_depth_multisampled_2d;
@group(0) @binding(3) var<uniform> parameters: ResolveParameters;

fn unpackVisibilityPair(value: vec4<u32>) -> vec2<u32> {
    return vec2<u32>(value.x | (value.y << 16u), value.z | (value.w << 16u));
}
struct ResolvedVisibility {
    @location(0) identity: vec2<u32>,
    @location(1) metadata: u32,
    @location(2) selection: u32,
    @builtin(frag_depth) depth: f32,
};
@vertex
fn advancedMsaaResolveVertex(@builtin(vertex_index) vertex: u32) -> @builtin(position) vec4<f32> {
    let corner = vec2<f32>(f32((vertex << 1u) & 2u), f32(vertex & 2u));
    return vec4<f32>(corner * 2.0 - 1.0, 0.0, 1.0);
}
@fragment
fn advancedMsaaResolveFragment(@builtin(position) position: vec4<f32>) -> ResolvedVisibility {
    let pixel = vec2<u32>(position.xy);
    if (any(pixel >= parameters.screenExtent)) { discard; }
    var result = ResolvedVisibility(vec2<u32>(0xffffffffu), 0xffffffffu, 0xffffffffu,
        select(1.0, 0.0, parameters.reversedDepth != 0u));
    var found = false;
    for (var sampleIndex = 0u; sampleIndex < 4u; sampleIndex++) {
        let identity = unpackVisibilityPair(textureLoad(rawVisibilityIdentity, pixel, sampleIndex));
        if (identity.x == 0u || identity.x == 0xffffffffu) { continue; }
        let depth = textureLoad(rawVisibilityDepth, pixel, sampleIndex);
        if ((bitcast<u32>(depth) & 0x7f800000u) == 0x7f800000u || depth < 0.0 || depth > 1.0) { continue; }
        let closer = select(depth < result.depth, depth > result.depth, parameters.reversedDepth != 0u);
        if (!found || closer) {
            found = true;
            let sidecars = unpackVisibilityPair(textureLoad(rawVisibilityMetadataSelection, pixel, sampleIndex));
            result.identity = identity;
            result.metadata = sidecars.x;
            result.selection = sidecars.y;
            result.depth = depth;
        }
    }
    return result;
}
