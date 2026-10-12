// The shared GPUScene LOD table uses projected world-sphere radius in pixels.
// Selection stays GPU-owned; authored raster candidates consume mesh + level.
struct LodParameters {
    boundsSphere: vec4<f32>,
    cameraPosition: vec4<f32>,
    projectionAndViewport: vec4<f32>,
    lod0MeshId: u32,
    lod1MeshId: u32,
    lod2MeshId: u32,
    lod3MeshId: u32,
    lod0MinRadius: f32,
    lod1MinRadius: f32,
    lod2MinRadius: f32,
    lod3MinRadius: f32,
    lodCount: u32,
    currentMeshId: u32,
    currentLod: u32,
    flags: u32,
};
@group(0) @binding(0) var<storage, read_write> selectedLod: array<u32>;
@group(0) @binding(1) var<uniform> parameters: LodParameters;

fn finite(value: f32) -> bool {
    return (bitcast<u32>(value) & 0x7f800000u) != 0x7f800000u;
}

@compute @workgroup_size(1, 1, 1)
fn meshletsSelectLod() {
    if (arrayLength(&selectedLod) < 4u) { return; }
    // Always overwrite all words, including empty/invalid publications.
    selectedLod[0] = 0u;
    selectedLod[1] = 0u;
    selectedLod[2] = 0u;
    selectedLod[3] = 0u;
    if (parameters.lodCount > 4u) { return; }
    if (parameters.lodCount <= 1u || (parameters.flags & (1u << 15u)) == 0u) {
        selectedLod[0] = parameters.currentMeshId;
        selectedLod[1] = parameters.currentLod;
        return;
    }
    let meshIds = array<u32, 4>(parameters.lod0MeshId, parameters.lod1MeshId, parameters.lod2MeshId, parameters.lod3MeshId);
    let thresholds = array<f32, 4>(parameters.lod0MinRadius, parameters.lod1MinRadius, parameters.lod2MinRadius, parameters.lod3MinRadius);
    let distance = max(length(parameters.cameraPosition.xyz - parameters.boundsSphere.xyz), 0.001);
    let pixelScale = max(abs(parameters.projectionAndViewport.x) * max(parameters.projectionAndViewport.z, 1.0),
                         abs(parameters.projectionAndViewport.y) * max(parameters.projectionAndViewport.w, 1.0)) * 0.5;
    let radius = max(parameters.boundsSphere.w, 0.0) * pixelScale / distance;
    var preferred = 0u;
    // Invalid input can only select the highest available detail.
    if (finite(distance) && finite(pixelScale) && finite(radius) && parameters.boundsSphere.w >= 0.0) {
        for (var level = 0u; level < parameters.lodCount; level++) {
            preferred = level;
            if (level == parameters.lodCount - 1u || radius >= thresholds[level]) { break; }
        }
    }
    var resolved = preferred;
    var mesh = meshIds[preferred];
    // Match the shared selector: prefer the nearest higher-detail resident
    // level, then search toward lower detail. Missing residency is never drawn.
    if (mesh == 0u) {
        for (var level = i32(preferred) - 1; level >= 0; level--) {
            if (meshIds[u32(level)] != 0u) {
                resolved = u32(level);
                mesh = meshIds[resolved];
                break;
            }
        }
    }
    if (mesh == 0u) {
        for (var level = preferred + 1u; level < parameters.lodCount; level++) {
            if (meshIds[level] != 0u) {
                resolved = level;
                mesh = meshIds[level];
                break;
            }
        }
    }
    selectedLod[0] = mesh;
    selectedLod[1] = resolved;
    selectedLod[2] = select(0u, 1u, mesh != parameters.currentMeshId || resolved != parameters.currentLod);
    selectedLod[3] = select(0u, 1u << preferred, resolved != preferred);
}
