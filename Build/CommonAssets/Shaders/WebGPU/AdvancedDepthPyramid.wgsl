// Advanced/Preparation/BuildDepthPyramid.comp's conservative 64x64 tiles.
// Every padded or invalid source depth resolves to far depth, never an occluder.
struct DepthParameters { extent: vec2<f32>, reversed: u32, reserved: u32 };
@group(0) @binding(0) var visibilityDepth: texture_depth_2d;
@group(0) @binding(1) var currentDepthPyramid: texture_storage_2d<r32float, write>;
@group(0) @binding(2) var<uniform> parameters: DepthParameters;
var<workgroup> depths: array<f32, 256>;
@compute @workgroup_size(256, 1, 1)
fn advancedDepthPyramid(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) local: u32) {
    let reversed = parameters.reversed != 0u;
    let farDepth = select(1.0, 0.0, reversed);
    var reduced = select(0.0, 1.0, reversed);
    for (var index = local; index < 4096u; index += 256u) {
        let source = group.xy * 64u + vec2<u32>(index % 64u, index / 64u);
        var depth = farDepth;
        if (all(source < vec2<u32>(parameters.extent))) { depth = textureLoad(visibilityDepth, vec2<i32>(source), 0); }
        if (!(depth >= 0.0 && depth <= 1.0)) { depth = farDepth; }
        reduced = select(max(reduced, depth), min(reduced, depth), reversed);
    }
    depths[local] = reduced;
    workgroupBarrier();
    for (var stride = 128u; stride > 0u; stride >>= 1u) {
        if (local < stride) {
            let left = depths[local];
            let right = depths[local + stride];
            depths[local] = select(max(left, right), min(left, right), reversed);
        }
        workgroupBarrier();
    }
    if (local == 0u) { textureStore(currentDepthPyramid, vec2<i32>(group.xy), vec4<f32>(depths[0])); }
}
