// Converts GPU tile membership counts to indirect compute arguments without CPU readback.
struct FinalizeParameters { cohortCount: u32, tileCapacity: u32, reserved0: u32, reserved1: u32 };
@group(0) @binding(0) var<storage, read> counts: array<u32>;
@group(0) @binding(1) var<storage, read_write> arguments: array<u32>;
@group(0) @binding(2) var<uniform> parameters: FinalizeParameters;
@compute @workgroup_size(64, 1, 1)
fn advancedShadeFinalize(@builtin(global_invocation_id) invocation: vec3<u32>) {
    let cohort = invocation.x;
    if (cohort >= parameters.cohortCount || cohort * 4u + 3u >= arrayLength(&counts) ||
        cohort * 4u + 3u >= arrayLength(&arguments)) { return; }
    let count = min(counts[cohort * 4u], parameters.tileCapacity);
    // Host admission guarantees capacity for every physical tile. Retain overflow
    // in ShadeCounts for diagnostics; never clamp a CPU-observed visibility count.
    arguments[cohort * 4u] = min(count, 65535u);
    arguments[cohort * 4u + 1u] = max((count + 65534u) / 65535u, 1u);
    arguments[cohort * 4u + 2u] = 1u;
    arguments[cohort * 4u + 3u] = cohort * parameters.tileCapacity;
}
