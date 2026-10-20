// Canonical ShadeBackground initializes every native output before cohort dispatches.
struct BackgroundParameters { screenExtent: vec2<u32>, reserved0: u32, reserved1: u32, backgroundColor: vec4<f32> };
@group(0) @binding(0) var visibilityIdentity: texture_2d<u32>;
@group(0) @binding(1) var hdrSceneColor: texture_storage_2d<rgba16float, write>;
@group(0) @binding(2) var velocity: texture_storage_2d<rgba16float, write>;
@group(0) @binding(3) var reactiveMask: texture_storage_2d<r32float, write>;
@group(0) @binding(4) var shadingDiagnostics: texture_storage_2d<r32uint, write>;
@group(0) @binding(5) var<uniform> parameters: BackgroundParameters;
@compute @workgroup_size(16, 16, 1)
fn advancedShadeBackground(@builtin(global_invocation_id) invocation: vec3<u32>) {
    if (any(invocation.xy >= parameters.screenExtent)) { return; }
    let pixel = vec2<i32>(invocation.xy);
    let draw = textureLoad(visibilityIdentity, pixel, 0).x;
    let background = draw == 0u || draw == 0xffffffffu;
    textureStore(hdrSceneColor, pixel, select(vec4<f32>(1.0, 0.0, 1.0, 1.0), vec4<f32>(parameters.backgroundColor.rgb, 0.0), background));
    textureStore(velocity, pixel, vec4<f32>(0.0));
    textureStore(reactiveMask, pixel, vec4<f32>(select(1.0, 0.0, background)));
    textureStore(shadingDiagnostics, pixel, vec4<u32>(select(1u << 16u, 255u << 8u, background)));
}
