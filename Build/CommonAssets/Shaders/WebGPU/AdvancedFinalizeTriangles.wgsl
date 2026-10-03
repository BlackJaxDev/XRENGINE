struct FinalizeParameters { argumentBase: u32, reserved0: u32, reserved1: u32, reserved2: u32 };
@group(0) @binding(0) var<storage, read_write> arguments: array<atomic<u32>>;
@group(0) @binding(1) var<uniform> parameters: FinalizeParameters;
@compute @workgroup_size(1, 1, 1)
fn advancedFinalizeTriangles() {
    // An invalid expansion must never expose stale triangle entries or a
    // partial object. The diagnostic word remains GPU-resident for shading.
    if (atomicLoad(&arguments[parameters.argumentBase + 4u]) != 0u) {
        atomicStore(&arguments[parameters.argumentBase], 0u);
    }
}
