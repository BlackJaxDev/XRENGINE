// BoundsGpu matches Commands/GPUIndirectRenderCommand.cs without introducing a second scene schema.
struct BoundsGpu {
    boundingSphere: vec4<f32>,
    aabbMin: vec4<f32>,
    aabbMax: vec4<f32>,
    version: u32, padding0: u32, padding1: u32, padding2: u32,
}
struct Draw {
    bounds: BoundsGpu,
    viewProjection: mat4x4<f32>,
    viewport: vec4<u32>,
    indexCount: u32, firstIndex: u32, flags: u32, padding: u32,
}
struct IndexedArguments {
    indexCount: u32, instanceCount: u32, firstIndex: u32, baseVertex: i32, firstInstance: u32,
}
struct Frame { count: u32, width: u32, height: u32, padding: u32 }
@group(0) @binding(0) var<storage, read> draws: array<Draw>;
@group(0) @binding(1) var<storage, read_write> arguments: array<IndexedArguments>;
@group(0) @binding(2) var<uniform> frame: Frame;
@group(0) @binding(3) var pyramid: texture_2d<f32>;

fn corner(bounds: BoundsGpu, index: u32) -> vec3<f32> {
    return select(bounds.aabbMin.xyz, bounds.aabbMax.xyz,
        vec3<bool>((index & 1u) != 0u, (index & 2u) != 0u, (index & 4u) != 0u));
}

fn finiteClip(clip: vec4<f32>) -> bool {
    return all(abs(clip) <= vec4<f32>(3.0e38));
}

fn inFrustum(draw: Draw) -> bool {
    var outsideMask = 63u;
    for (var i = 0u; i < 8u; i++) {
        let clip = draw.viewProjection * vec4<f32>(corner(draw.bounds, i), 1.0);
        if (!finiteClip(clip)) { return true; }
        let epsilon = 0.00001 * max(abs(clip.w), 1.0);
        let cornerMask = select(0u, 1u, clip.x < -clip.w - epsilon)
            | select(0u, 2u, clip.x > clip.w + epsilon)
            | select(0u, 4u, clip.y < -clip.w - epsilon)
            | select(0u, 8u, clip.y > clip.w + epsilon)
            | select(0u, 16u, clip.z < -epsilon)
            | select(0u, 32u, clip.z > clip.w + epsilon);
        outsideMask &= cornerMask;
    }
    return outsideMask == 0u;
}

// Port of GPURenderOcclusionHiZ.comp's conservative eight-corner AABB test.
// Browser profile uses normal Z, WebGPU's [0,1] depth and top-left texture coordinates.
fn occluded(draw: Draw) -> bool {
    var uvMin = vec2<f32>(1.0);
    var uvMax = vec2<f32>(0.0);
    var nearestDepth = 1.0;
    for (var i = 0u; i < 8u; i++) {
        let clip = draw.viewProjection * vec4<f32>(corner(draw.bounds, i), 1.0);
        if (!finiteClip(clip) || clip.w <= 0.0) { return false; }
        let ndc = clip.xyz / clip.w;
        let uv = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
        if (ndc.z < 0.0 || ndc.z > 1.0 || any(uv < vec2<f32>(0.0)) || any(uv > vec2<f32>(1.0))) { return false; }
        uvMin = min(uvMin, uv); uvMax = max(uvMax, uv);
        nearestDepth = min(nearestDepth, ndc.z);
    }
    let extent = vec2<f32>(f32(frame.width), f32(frame.height));
    let viewportOrigin = vec2<f32>(draw.viewport.xy);
    let viewportExtent = vec2<f32>(draw.viewport.zw);
    // Include a pixel of raster uncertainty; viewport-edge uncertainty stays visible.
    let firstPixel = floor(viewportOrigin + uvMin * viewportExtent) - vec2<f32>(1.0);
    let lastPixel = ceil(viewportOrigin + uvMax * viewportExtent) + vec2<f32>(1.0);
    if (any(firstPixel < viewportOrigin) || any(lastPixel >= viewportOrigin + viewportExtent)) { return false; }
    let fullMin = firstPixel / extent;
    let fullMax = lastPixel / extent;
    let pixelSpan = lastPixel - firstPixel;
    let target = max(max(pixelSpan.x, pixelSpan.y) / 6.0, 1.0);
    let mip = min(u32(floor(log2(target))), textureNumLevels(pyramid) - 1u);
    let size = textureDimensions(pyramid, mip);
    let first = min(vec2<u32>(floor(fullMin * vec2<f32>(size))), size - vec2<u32>(1u));
    let last = min(vec2<u32>(floor(fullMax * vec2<f32>(size))), size - vec2<u32>(1u));
    let span = last - first + vec2<u32>(1u);
    if (any(span > vec2<u32>(8u))) { return false; }
    var farthest = 0.0;
    for (var y = 0u; y < 8u; y++) {
        if (y >= span.y) { break; }
        for (var x = 0u; x < 8u; x++) {
            if (x >= span.x) { break; }
            let depth = textureLoad(pyramid, vec2<i32>(first + vec2<u32>(x, y)), i32(mip)).x;
            if (!(depth >= 0.0 && depth <= 1.0)) { return false; }
            farthest = max(farthest, depth);
        }
    }
    return nearestDepth > farthest + 0.0001;
}

fn writeVisibility(index: u32, useHiZ: bool) {
    if (index >= frame.count) { return; }
    let draw = draws[index];
    var visible = (draw.flags & 2u) == 0u;
    // Designated occluders contributed this frame's depth and cannot reject themselves.
    if (visible && (draw.flags & 12u) == 0u) {
        visible = inFrustum(draw);
        if (visible && useHiZ) { visible = !occluded(draw); }
    }
    // Every submitted slot is fully overwritten, including empty/culled commands.
    // Per-draw instance-buffer slices keep firstInstance zero on every WebGPU device.
    arguments[index] = IndexedArguments(draw.indexCount, select(0u, 1u, visible), draw.firstIndex, 0, 0u);
}

@compute @workgroup_size(64)
fn frustumMain(@builtin(global_invocation_id) id: vec3<u32>) { writeVisibility(id.x, false); }

@compute @workgroup_size(64)
fn occlusionMain(@builtin(global_invocation_id) id: vec3<u32>) { writeVisibility(id.x, true); }
