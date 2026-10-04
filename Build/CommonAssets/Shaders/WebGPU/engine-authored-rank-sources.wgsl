// Rank bounded authored sources without CPU readback or rewriting source records.
struct RankParameters {
    cameraPosition: vec4<f32>,
    sourceCount: u32,
    sortPolicy: u32,
    usePriority: u32,
    reserved: u32,
};
@group(0) @binding(0) var<storage, read> sources: array<u32>;
@group(0) @binding(1) var<storage, read_write> ranks: array<u32>;
@group(0) @binding(2) var<uniform> parameters: RankParameters;

fn sourceFloat(sourceIndex: u32, word: u32) -> f32 {
    return bitcast<f32>(sources[sourceIndex * 16u + word]);
}

fn isNan(value: f32) -> bool {
    let bits = bitcast<u32>(value);
    return (bits & 0x7f800000u) == 0x7f800000u && (bits & 0x007fffffu) != 0u;
}

fn distanceSquared(sourceIndex: u32) -> f32 {
    let camera = parameters.cameraPosition.xyz;
    var delta = vec3<f32>(0.0);
    if (sources[sourceIndex * 16u + 3u] != 0u) {
        let lower = vec3<f32>(sourceFloat(sourceIndex, 0u), sourceFloat(sourceIndex, 1u), sourceFloat(sourceIndex, 2u));
        let upper = vec3<f32>(sourceFloat(sourceIndex, 4u), sourceFloat(sourceIndex, 5u), sourceFloat(sourceIndex, 6u));
        // A NaN bound is an unknown distance, ordered like .NET Single.CompareTo.
        if (any(vec3<bool>(isNan(lower.x) || isNan(upper.x) || isNan(camera.x),
                           isNan(lower.y) || isNan(upper.y) || isNan(camera.y),
                           isNan(lower.z) || isNan(upper.z) || isNan(camera.z)))) {
            return bitcast<f32>(0x7fc00000u);
        }
        delta = max(max(lower - camera, camera - upper), vec3<f32>(0.0));
    } else {
        let position = vec3<f32>(sourceFloat(sourceIndex, 8u), sourceFloat(sourceIndex, 9u), sourceFloat(sourceIndex, 10u));
        if (any(vec3<bool>(isNan(position.x) || isNan(camera.x),
                           isNan(position.y) || isNan(camera.y),
                           isNan(position.z) || isNan(camera.z)))) {
            return bitcast<f32>(0x7fc00000u);
        }
        delta = position - camera;
    }
    return dot(delta, delta);
}

fn before(left: u32, right: u32) -> bool {
    let leftBase = left * 16u;
    let rightBase = right * 16u;
    if (parameters.usePriority != 0u) {
        let leftPriority = bitcast<i32>(sources[leftBase + 11u]);
        let rightPriority = bitcast<i32>(sources[rightBase + 11u]);
        if (leftPriority != rightPriority) { return leftPriority < rightPriority; }
    }
    if (parameters.sortPolicy == 1u || parameters.sortPolicy == 2u) {
        let leftDistance = distanceSquared(left);
        let rightDistance = distanceSquared(right);
        let leftNan = isNan(leftDistance);
        let rightNan = isNan(rightDistance);
        if (leftNan != rightNan) {
            // Single.CompareTo orders NaN before all other values. Sort direction reverses it.
            return select(leftNan, rightNan, parameters.sortPolicy == 2u);
        }
        if (!leftNan && leftDistance != rightDistance) {
            return select((leftDistance < rightDistance), (leftDistance > rightDistance), parameters.sortPolicy == 2u);
        }
    }
    let leftHigh = sources[leftBase + 13u];
    let rightHigh = sources[rightBase + 13u];
    if (leftHigh != rightHigh) { return leftHigh < rightHigh; }
    let leftLow = sources[leftBase + 12u];
    let rightLow = sources[rightBase + 12u];
    if (leftLow != rightLow) { return leftLow < rightLow; }
    // Invalid duplicate insertion tokens remain deterministic; the host rejects them.
    return left < right;
}

@compute @workgroup_size(64, 1, 1)
fn authoredRankSources(@builtin(global_invocation_id) invocation: vec3<u32>) {
    let sourceIndex = invocation.x;
    if (parameters.sourceCount > 64u || sourceIndex >= parameters.sourceCount ||
        arrayLength(&sources) / 16u < parameters.sourceCount || arrayLength(&ranks) < parameters.sourceCount) {
        return;
    }
    var rank = 0u;
    for (var other = 0u; other < parameters.sourceCount; other++) {
        if (other != sourceIndex && before(other, sourceIndex)) { rank += 1u; }
    }
    ranks[sourceIndex] = rank;
}
