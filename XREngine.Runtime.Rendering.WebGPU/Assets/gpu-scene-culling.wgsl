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
struct Frame { count: u32, width: u32, height: u32, viewCount: u32 }
@group(0) @binding(0) var<storage, read> draws: array<Draw>;
@group(0) @binding(1) var<storage, read_write> arguments: array<IndexedArguments>;
@group(0) @binding(2) var<uniform> frame: Frame;
@group(0) @binding(3) var pyramid: texture_2d<f32>;
@group(0) @binding(4) var<storage, read> hierarchy: array<u32>;
@group(0) @binding(5) var<storage, read> mortonObjects: array<vec2<u32>>;
@group(0) @binding(6) var<storage, read> viewRanges: array<vec4<u32>>;
@group(0) @binding(7) var<storage, read_write> hierarchyFaults: array<atomic<u32>>;

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

@compute @workgroup_size(64)
fn hierarchyArgumentsMain(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x >= frame.count) { return; }
    let draw = draws[id.x];
    let visible = (draw.flags & 2u) == 0u && (draw.flags & 12u) != 0u;
    arguments[id.x] = IndexedArguments(draw.indexCount, select(0u, 1u, visible), draw.firstIndex, 0, 0u);
}

fn keepRangeVisible(first: u32, count: u32) {
    for (var i = first; i < first + count; i++) {
        arguments[i].instanceCount = select(1u, 0u, (draws[i].flags & 2u) != 0u);
    }
}

// Root-down traversal of the canonical compact GpuBvhNode layout. Each view
// owns a disjoint range of draw slots; Morton sorting never changes that identity.
@compute @workgroup_size(1)
fn hierarchyMain(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x >= frame.viewCount) { return; }
    let range = viewRanges[id.x]; let first = range.x; let count = range.y;
    if (count == 0u || first >= frame.count || count > frame.count - first) { return; }
    let nodeCount = hierarchy[0]; let root = hierarchy[1];
    if (atomicLoad(&hierarchyFaults[0]) != 0u || atomicLoad(&hierarchyFaults[1]) != 0u
        || hierarchy[2] != 12u || hierarchy[3] != 1u || nodeCount != frame.count * 2u - 1u || root >= nodeCount) {
        keepRangeVisible(first, count); return;
    }
    var stack: array<u32, 128>;
    stack[0] = root; var length = 1u; var visited = 0u; var malformed = false;
    var candidate = draws[first];
    loop {
        if (length == 0u) { break; }
        if (visited >= nodeCount) { malformed = true; break; }
        visited++; length--;
        let index = stack[length];
        if (index >= nodeCount) { malformed = true; break; }
        let at = 4u + index * 12u;
        candidate.bounds.aabbMin = vec4<f32>(bitcast<f32>(hierarchy[at]), bitcast<f32>(hierarchy[at + 1u]), bitcast<f32>(hierarchy[at + 2u]), 0.0);
        candidate.bounds.aabbMax = vec4<f32>(bitcast<f32>(hierarchy[at + 4u]), bitcast<f32>(hierarchy[at + 5u]), bitcast<f32>(hierarchy[at + 6u]), 0.0);
        if (!inFrustum(candidate)) { continue; }
        if ((hierarchy[at + 11u] & 1u) != 0u) {
            let primitive = hierarchy[at + 8u];
            if (hierarchy[at + 9u] != 1u || primitive >= frame.count) { malformed = true; break; }
            let objectId = mortonObjects[primitive].y;
            if (objectId >= frame.count) { malformed = true; break; }
            if (objectId >= first && objectId - first < count && (draws[objectId].flags & 2u) == 0u)
                { arguments[objectId].instanceCount = 1u; }
        } else {
            if (length + 2u > 128u) { malformed = true; break; }
            stack[length] = hierarchy[at + 3u]; stack[length + 1u] = hierarchy[at + 7u]; length += 2u;
        }
    }
    if (malformed) { atomicOr(&hierarchyFaults[1], 4u); keepRangeVisible(first, count); }
}

@compute @workgroup_size(64)
fn hierarchyOcclusionMain(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x >= frame.count || arguments[id.x].instanceCount == 0u) { return; }
    let draw = draws[id.x];
    if ((draw.flags & 12u) == 0u && occluded(draw)) { arguments[id.x].instanceCount = 0u; }
}

@compute @workgroup_size(64)
fn hierarchyResolveMain(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x >= frame.count) { return; }
    if (atomicLoad(&hierarchyFaults[0]) != 0u || atomicLoad(&hierarchyFaults[1]) != 0u)
        { arguments[id.x].instanceCount = select(1u, 0u, (draws[id.x].flags & 2u) != 0u); }
}
