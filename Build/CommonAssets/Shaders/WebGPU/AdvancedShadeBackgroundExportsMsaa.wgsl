// Canonical uncovered MSAA sidecars are zero, including normal and RMSE.
struct ExportParameters { screenExtent: vec2<u32>, reserved0: u32, reserved1: u32 };
@group(0) @binding(0) var emissionColor: texture_storage_2d<rgba16float, write>;
@group(0) @binding(1) var albedoOpacity: texture_storage_2d<rgba16float, write>;
@group(0) @binding(2) var normal: texture_storage_2d<rgba16float, write>;
@group(0) @binding(3) var rmse: texture_storage_2d<rgba16float, write>;
@group(0) @binding(4) var<uniform> parameters: ExportParameters;
@compute @workgroup_size(16, 16, 1)
fn advancedShadeBackgroundExports(@builtin(global_invocation_id) invocation: vec3<u32>) {
    if (any(invocation.xy >= parameters.screenExtent)) { return; }
    let pixel = vec2<i32>(invocation.xy);
    textureStore(emissionColor, pixel, vec4<f32>(0.0));
    textureStore(albedoOpacity, pixel, vec4<f32>(0.0));
    textureStore(normal, pixel, vec4<f32>(0.0));
    textureStore(rmse, pixel, vec4<f32>(0.0));
}
