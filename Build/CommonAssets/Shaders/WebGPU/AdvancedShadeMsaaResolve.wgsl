// Resolve cohort-owned f32 samples in canonical sample order, then quantize once.
struct ResolveParameters { screenExtent: vec2<u32>, reserved0: u32, reserved1: u32, backgroundColor: vec4<f32> };
@group(0) @binding(0) var rawVisibilityIdentity: texture_multisampled_2d<u32>;
@group(0) @binding(1) var rawVisibilityMetadataSelection: texture_multisampled_2d<u32>;
@group(0) @binding(2) var sampleRadianceReactive: texture_2d_array<f32>;
@group(0) @binding(3) var hdrSceneColor: texture_storage_2d<rgba16float, write>;
@group(0) @binding(4) var reactiveMask: texture_storage_2d<r32float, write>;
@group(0) @binding(5) var velocity: texture_storage_2d<rgba16float, write>;
@group(0) @binding(6) var shadingDiagnostics: texture_storage_2d<r32uint, write>;
@group(0) @binding(7) var<uniform> parameters: ResolveParameters;

fn unpackVisibilityPair(value: vec4<u32>) -> vec2<u32> {
    return vec2<u32>(value.x | (value.y << 16u), value.z | (value.w << 16u));
}
@compute @workgroup_size(16, 16, 1)
fn advancedShadeMsaaResolve(@builtin(global_invocation_id) invocation: vec3<u32>) {
    let pixel = invocation.xy;
    if (any(pixel >= parameters.screenExtent)) { return; }
    var covered = 0u;
    var radiance = vec3<f32>(0.0);
    var reactive = 0.0;
    var firstTuple = vec4<u32>(0u);
    var mixed = false;
    for (var sampleIndex = 0u; sampleIndex < 4u; sampleIndex++) {
        let identity = unpackVisibilityPair(textureLoad(rawVisibilityIdentity, pixel, sampleIndex));
        if (identity.x == 0u || identity.x == 0xffffffffu) { continue; }
        let sidecars = unpackVisibilityPair(textureLoad(rawVisibilityMetadataSelection, pixel, sampleIndex));
        let tuple = vec4<u32>(identity, sidecars);
        if (covered == 0u) { firstTuple = tuple; }
        else { mixed = mixed || any(tuple != firstTuple); }
        let sample = textureLoad(sampleRadianceReactive, pixel, sampleIndex, 0);
        radiance += sample.rgb;
        reactive = max(reactive, sample.a);
        covered++;
    }
    let coverage = f32(covered) * 0.25;
    let background = max(parameters.backgroundColor.rgb, vec3<f32>(0.0));
    textureStore(hdrSceneColor, pixel, vec4<f32>(radiance * 0.25 + background * (1.0 - coverage), coverage));
    if (covered == 0u) {
        textureStore(velocity, pixel, vec4<f32>(0.0));
        textureStore(shadingDiagnostics, pixel, vec4<u32>(0u));
        reactive = 0.0;
    } else if (covered != 4u || mixed) {
        reactive = 1.0;
    }
    textureStore(reactiveMask, pixel, vec4<f32>(round(clamp(reactive, 0.0, 1.0) * 255.0) / 255.0));
}
