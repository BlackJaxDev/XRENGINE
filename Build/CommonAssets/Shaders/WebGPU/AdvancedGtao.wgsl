// Advanced/AO/Gtao.comp's depth-derived GTAO, using the frozen canonical view.
// The physical R32F output preserves the logical R8 normalized visibility.
struct ViewParameters {
    view: mat4x4<f32>,
    inverseViewProjection: mat4x4<f32>,
    projectionUnjittered: mat4x4<f32>,
    renderSizeAndInverse: vec4<f32>,
    depthParams: vec4<f32>,
    flags: u32,
    enabled: u32,
};
@group(0) @binding(0) var visibilityDepth: texture_depth_2d;
@group(0) @binding(1) var ambientOcclusion: texture_storage_2d<r32float, write>;
@group(0) @binding(2) var<uniform> parameters: ViewParameters;

const PI = 3.14159265359;
const HALF_PI = 1.57079632679;
const RADIUS = 2.2;
const BIAS = 0.06;
const FALLOFF_START_RATIO = 0.4;
const THICKNESS_RATIO = 1.0;
const SLICE_COUNT = 3;
const STEPS_PER_SLICE = 4;

struct VectorResult { value: vec3<f32>, valid: bool };
fn finite(x: f32) -> bool { return (bitcast<u32>(x) & 0x7f800000u) != 0x7f800000u; }
fn finite3(x: vec3<f32>) -> bool {
    return all((bitcast<vec3<u32>>(x) & vec3<u32>(0x7f800000u)) != vec3<u32>(0x7f800000u));
}
fn reversedDepth() -> bool { return (parameters.flags & 2u) != 0u || parameters.depthParams.w != 0.0; }
fn viewPosition(pixel: vec2<i32>) -> VectorResult {
    let depth = textureLoad(visibilityDepth, pixel, 0);
    if (!finite(depth) || select(depth >= 1.0, depth <= 0.0, reversedDepth())) {
        return VectorResult(vec3<f32>(0.0), false);
    }
    var uv = (vec2<f32>(pixel) + 0.5) * parameters.renderSizeAndInverse.zw;
    if ((parameters.flags & 64u) != 0u) { uv.y = 1.0 - uv.y; }
    let clipDepth = select(depth * 2.0 - 1.0, depth, (parameters.flags & 1u) != 0u);
    let homogeneous = parameters.inverseViewProjection * vec4<f32>(uv * 2.0 - 1.0, clipDepth, 1.0);
    let world = homogeneous.xyz / homogeneous.w;
    let position = (parameters.view * vec4<f32>(world, 1.0)).xyz;
    return VectorResult(position, finite3(world) && finite3(position));
}
fn shortestCenterEdge(negative: VectorResult, positive: VectorResult, center: vec3<f32>) -> VectorResult {
    var negativeLength = -1.0;
    var positiveLength = -1.0;
    var negativeEdge = vec3<f32>(0.0);
    var positiveEdge = vec3<f32>(0.0);
    if (negative.valid) {
        negativeEdge = center - negative.value;
        negativeLength = dot(negativeEdge, negativeEdge);
        if (!finite3(negativeEdge) || !finite(negativeLength) || negativeLength <= 1.0e-10) { negativeLength = -1.0; }
    }
    if (positive.valid) {
        positiveEdge = positive.value - center;
        positiveLength = dot(positiveEdge, positiveEdge);
        if (!finite3(positiveEdge) || !finite(positiveLength) || positiveLength <= 1.0e-10) { positiveLength = -1.0; }
    }
    if (negativeLength < 0.0 && positiveLength < 0.0) { return VectorResult(vec3<f32>(0.0), false); }
    var direction: vec3<f32>;
    if (positiveLength < 0.0 || (negativeLength >= 0.0 && negativeLength <= positiveLength)) {
        direction = negativeEdge * inverseSqrt(negativeLength);
    } else {
        direction = positiveEdge * inverseSqrt(positiveLength);
    }
    return VectorResult(direction, finite3(direction));
}
fn depthNormal(pixel: vec2<i32>, extent: vec2<i32>, center: vec3<f32>, viewDirection: vec3<f32>) -> VectorResult {
    let left = viewPosition(max(pixel - vec2<i32>(1, 0), vec2<i32>(0)));
    let right = viewPosition(min(pixel + vec2<i32>(1, 0), extent - 1));
    let up = viewPosition(max(pixel - vec2<i32>(0, 1), vec2<i32>(0)));
    let down = viewPosition(min(pixel + vec2<i32>(0, 1), extent - 1));
    let xAxis = shortestCenterEdge(left, right, center);
    let yAxis = shortestCenterEdge(up, down, center);
    if (!xAxis.valid || !yAxis.valid) { return VectorResult(vec3<f32>(0.0, 0.0, 1.0), false); }
    let axis = cross(xAxis.value, yAxis.value);
    let lengthSquared = dot(axis, axis);
    if (!finite3(axis) || !finite(lengthSquared) || lengthSquared <= 1.0e-8) {
        return VectorResult(vec3<f32>(0.0, 0.0, 1.0), false);
    }
    var normal = axis * inverseSqrt(lengthSquared);
    if (dot(normal, viewDirection) < 0.0) { normal = -normal; }
    return VectorResult(normal, finite3(normal));
}
fn fastAcos(x: f32) -> f32 {
    let a = abs(x);
    var result = (-0.0187293 * a + 0.0742610) * a - 0.2121144;
    result = (result * a + 1.5707288) * sqrt(max(1.0 - a, 0.0));
    return select(result, PI - result, x < 0.0);
}
fn integrateArc(horizon: f32, normal: f32) -> f32 {
    let h = clamp(horizon, 0.0, HALF_PI);
    let n = clamp(normal, -HALF_PI, HALF_PI);
    return 0.25 * (-cos(2.0 * h - n) + cos(n) + 2.0 * h * sin(n));
}
fn noise(pixel: vec2<i32>) -> f32 {
    return fract(52.9829189 * fract(dot(vec2<f32>(pixel), vec2<f32>(0.06711056, 0.00583715))));
}
fn writeVisibility(pixel: vec2<i32>, value: f32) {
    textureStore(ambientOcclusion, pixel, vec4<f32>(round(clamp(value, 0.0, 1.0) * 255.0) / 255.0));
}
@compute @workgroup_size(16, 16, 1)
fn advancedGtao(@builtin(global_invocation_id) invocation: vec3<u32>) {
    let pixel = vec2<i32>(invocation.xy);
    let extent = vec2<i32>(textureDimensions(visibilityDepth));
    if (any(invocation.xy >= vec2<u32>(parameters.renderSizeAndInverse.xy))) { return; }
    if (parameters.enabled == 0u) { writeVisibility(pixel, 1.0); return; }
    let centerResult = viewPosition(pixel);
    if (!centerResult.valid) { writeVisibility(pixel, 1.0); return; }
    let center = centerResult.value;
    let centerDepth = abs(center.z);
    if (!finite(centerDepth) || centerDepth <= 1.0e-4) { writeVisibility(pixel, 1.0); return; }
    let centerLength = dot(center, center);
    if (!finite(centerLength) || centerLength <= 1.0e-10) { writeVisibility(pixel, 1.0); return; }
    let viewDirection = -center * inverseSqrt(centerLength);
    let projectionX = abs(parameters.projectionUnjittered[0][0]);
    let projectionY = abs(parameters.projectionUnjittered[1][1]);
    if (!finite(projectionX) || !finite(projectionY) || projectionX <= 1.0e-5 || projectionY <= 1.0e-5) { writeVisibility(pixel, 1.0); return; }
    let normalResult = depthNormal(pixel, extent, center, viewDirection);
    if (!normalResult.valid) { writeVisibility(pixel, 1.0); return; }
    let normal = normalResult.value;
    let radiusPixels = clamp(RADIUS * projectionY * 0.5 * f32(extent.y) / centerDepth, 1.0, 192.0);
    let textureYSign = select(1.0, -1.0, (parameters.flags & 64u) != 0u);
    var visibility = 0.0;
    let baseAngle = (noise(pixel) - 0.5) * PI / f32(SLICE_COUNT);
    let jitter = noise(pixel + vec2<i32>(17, 43));
    for (var slice = 0; slice < SLICE_COUNT; slice++) {
        let angle = baseAngle + PI * f32(slice) / f32(SLICE_COUNT);
        let ray = vec2<f32>(cos(angle), sin(angle));
        var tangentSeed = vec3<f32>(ray.x / projectionX, ray.y * textureYSign / projectionY, 0.0);
        let tangentSeedLength = dot(tangentSeed, tangentSeed);
        if (!finite3(tangentSeed) || !finite(tangentSeedLength) || tangentSeedLength <= 1.0e-10) { visibility += 1.0; continue; }
        tangentSeed *= inverseSqrt(tangentSeedLength);
        let tangentProjection = tangentSeed - viewDirection * dot(tangentSeed, viewDirection);
        let tangentLength = dot(tangentProjection, tangentProjection);
        if (!finite3(tangentProjection) || !finite(tangentLength) || tangentLength <= 1.0e-10) { visibility += 1.0; continue; }
        let tangent = tangentProjection * inverseSqrt(tangentLength);
        let planeAxis = cross(viewDirection, tangent);
        let planeLength = dot(planeAxis, planeAxis);
        if (!finite3(planeAxis) || !finite(planeLength) || planeLength <= 1.0e-10) { visibility += 1.0; continue; }
        let plane = planeAxis * inverseSqrt(planeLength);
        var projectedNormal = normal - plane * dot(normal, plane);
        let projectedLength = length(projectedNormal);
        if (!finite(projectedLength) || projectedLength <= 1.0e-5) { visibility += 1.0; continue; }
        projectedNormal /= projectedLength;
        let gamma = atan2(dot(projectedNormal, tangent), dot(projectedNormal, viewDirection));
        var forward = HALF_PI;
        var backward = HALF_PI;
        for (var step = 0; step < STEPS_PER_SLICE; step++) {
            let offset = vec2<i32>(round(ray * radiusPixels * ((f32(step) + jitter) / f32(STEPS_PER_SLICE))));
            for (var side = -1; side <= 1; side += 2) {
                let samplePixel = pixel + offset * side;
                if (any(samplePixel < vec2<i32>(0)) || any(samplePixel >= extent)) { continue; }
                let sample = viewPosition(samplePixel);
                if (!sample.valid) { continue; }
                let delta = sample.value - center;
                let distance = length(delta);
                if (!finite(distance) || distance <= 1.0e-4 || distance > RADIUS || max(0.0, -dot(delta, viewDirection)) > RADIUS * THICKNESS_RATIO) { continue; }
                let inPlane = delta - plane * dot(delta, plane);
                let inPlaneLength = length(inPlane);
                if (!finite(inPlaneLength) || inPlaneLength <= 1.0e-5) { continue; }
                var horizon = fastAcos(clamp(dot(inPlane / inPlaneLength, viewDirection), 0.0, 1.0));
                horizon = mix(HALF_PI, horizon, 1.0 - smoothstep(FALLOFF_START_RATIO * RADIUS, RADIUS, distance));
                horizon = clamp(horizon + BIAS, 0.0, HALF_PI);
                if (side > 0) { forward = min(forward, horizon); } else { backward = min(backward, horizon); }
            }
        }
        visibility += clamp(projectedLength * (integrateArc(forward, gamma) + integrateArc(backward, -gamma)), 0.0, 1.0);
    }
    visibility = clamp(visibility / f32(SLICE_COUNT), 0.0, 1.0);
    let edge = min(vec2<f32>(pixel) + 0.5, vec2<f32>(extent) - (vec2<f32>(pixel) + 0.5));
    visibility = mix(1.0, visibility, smoothstep(0.0, clamp(radiusPixels * 0.08, 2.0, 8.0), min(edge.x, edge.y)));
    writeVisibility(pixel, visibility);
}
