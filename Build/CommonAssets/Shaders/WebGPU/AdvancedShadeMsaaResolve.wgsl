// Resolve cohort-owned f32 samples in canonical sample order, then quantize once.
// reserved0 carries EAntiAliasingFilterMode: 0 = Default, 1 = Box1x1, 2 = Tent2x2.
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

fn standardMsaa4xOffset(sampleIndex: u32) -> vec2<f32> {
    switch (sampleIndex) {
        case 0u: { return vec2<f32>(-0.125, -0.375); }
        case 1u: { return vec2<f32>( 0.375, -0.125); }
        case 2u: { return vec2<f32>(-0.375,  0.125); }
        default: { return vec2<f32>( 0.125,  0.375); }
    }
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
    let background = max(parameters.backgroundColor.rgb, vec3<f32>(0.0));
    var resolvedColor = radiance * 0.25 + background * (1.0 - f32(covered) * 0.25);
    var resolvedCoverage = f32(covered) * 0.25;
    if (parameters.reserved0 == 2u) {
        let extent = vec2<i32>(parameters.screenExtent);
        let center = vec2<i32>(pixel);
        var colorSum = vec3<f32>(0.0);
        var coveredWeight = 0.0;
        var totalWeight = 0.0;
        for (var oy = -1; oy <= 1; oy++) {
            for (var ox = -1; ox <= 1; ox++) {
                let samplePixel = center + vec2<i32>(ox, oy);
                if (any(samplePixel < vec2<i32>(0)) || any(samplePixel >= extent)) { continue; }
                let sampleCoord = vec2<u32>(samplePixel);
                for (var s = 0u; s < 4u; s++) {
                    let delta = vec2<f32>(f32(ox), f32(oy)) + standardMsaa4xOffset(s);
                    let wx = 1.0 - abs(delta.x);
                    let wy = 1.0 - abs(delta.y);
                    if (wx <= 0.0 || wy <= 0.0) { continue; }
                    let weight = wx * wy;
                    let identity = unpackVisibilityPair(textureLoad(rawVisibilityIdentity, sampleCoord, s));
                    if (identity.x == 0u || identity.x == 0xffffffffu) {
                        colorSum += background * weight;
                    } else {
                        let sample = textureLoad(sampleRadianceReactive, sampleCoord, s, 0);
                        colorSum += sample.rgb * weight;
                        coveredWeight += weight;
                    }
                    totalWeight += weight;
                }
            }
        }
        let invWeight = 1.0 / max(totalWeight, 1e-6);
        resolvedColor = colorSum * invWeight;
        resolvedCoverage = clamp(coveredWeight * invWeight, 0.0, 1.0);
    }
    textureStore(hdrSceneColor, pixel, vec4<f32>(resolvedColor, resolvedCoverage));
    if (covered == 0u) {
        textureStore(velocity, pixel, vec4<f32>(0.0));
        textureStore(shadingDiagnostics, pixel, vec4<u32>(0u));
        reactive = select(0.0, 1.0, resolvedCoverage > 1e-4);
    } else if (covered != 4u || mixed || resolvedCoverage < 0.999) {
        reactive = 1.0;
    }
    textureStore(reactiveMask, pixel, vec4<f32>(round(clamp(reactive, 0.0, 1.0) * 255.0) / 255.0));
}
