// Backend lowering of Advanced/Preparation/AggregateDeformation.comp. Canonical
// records share one input binding so aggregate jobs fit mobile storage limits.
struct Parameters {
    firstGroupedJob: u32,
    groupedJobCount: u32,
    batchVertexCount: u32,
    reserved0: u32,
};

@group(0) @binding(0) var<storage, read> data: array<u32>;
@group(0) @binding(1) var<storage, read_write> current: array<u32>;
@group(0) @binding(2) var<storage, read_write> authoredBasis: array<u32>;
@group(0) @binding(3) var<uniform> parameters: Parameters;

const INVALID = 0xffffffffu;
const HEADER_WORDS = 56u;
const FEATURE_SKINNING = 1u;
const FEATURE_BLENDSHAPES = 2u;
const FEATURE_NORMALS = 4u;
const FEATURE_TANGENTS = 8u;
const FEATURE_SPILL_INFLUENCES = 16u;
const FEATURE_MAXIMUM_BLENDSHAPE = 128u;
const FEATURE_PRECOMPOSED_PALETTE = 512u;

// Each directory row is {wordOffset, exactByteLength, reserved0, reserved1}.
// Sections are Jobs, GroupedJobIndices, GroupedJobVertexOffsets, SourceVertices,
// SkinInfluences, Palette, InverseBind, ActiveShapes, Deltas, Spill, ShapeRanges,
// ShapeRecords and JobControls. Palette records each contain three vec4 rows;
// JobControls is {influenceCap: u32, morphThreshold: f32}, indexed by job index.
// Browser-only section 13 is raw normal.xyz, flags, tangent.xyzw. The first
// thirteen sections and the canonical output remain byte-for-byte unchanged.
const STRIDES = array<u32, 14>(28u, 1u, 1u, 16u, 12u, 12u, 12u, 2u, 4u, 2u, 4u, 4u, 2u, 8u);

struct DeformationJob {
    sourceVertexOffset: u32,
    currentVertexOffset: u32,
    boneInfluenceOffset: u32,
    bonePaletteOffset: u32,
    inverseBindOffset: u32,
    blendshapeWeightOffset: u32,
    blendshapeShapeOffset: u32,
    vertexFirst: u32,
    vertexCount: u32,
    boneCount: u32,
    blendshapeCount: u32,
    features: u32,
    order: u32,
    outputStride: u32,
};

struct Deformed {
    position: vec3<f32>,
    normal: vec3<f32>,
    tangent: vec3<f32>,
    authoredNormal: vec3<f32>,
    authoredTangent: vec3<f32>,
};

struct MorphResult {
    value: Deformed,
    valid: bool,
};

struct Weighted {
    value: Deformed,
    total: f32,
};

fn sectionsValid() -> bool {
    let length = arrayLength(&data);
    if (length < HEADER_WORDS) { return false; }
    for (var stream = 0u; stream < 14u; stream++) {
        let offset = data[stream * 4u];
        let bytes = data[stream * 4u + 1u];
        if ((bytes & 3u) != 0u || offset < HEADER_WORDS || offset > length) { return false; }
        let words = bytes / 4u;
        if (words > length - offset || words % STRIDES[stream] != 0u) { return false; }
    }
    return true;
}

fn sectionCount(stream: u32) -> u32 {
    return data[stream * 4u + 1u] / (STRIDES[stream] * 4u);
}

fn rangeValid(stream: u32, first: u32, count: u32) -> bool {
    let length = sectionCount(stream);
    return first <= length && count <= length - first;
}

fn address(stream: u32, index: u32) -> u32 {
    return data[stream * 4u] + index * STRIDES[stream];
}

fn f(word: u32) -> f32 { return bitcast<f32>(data[word]); }
fn v3(word: u32) -> vec3<f32> { return vec3<f32>(f(word), f(word + 1u), f(word + 2u)); }
fn v4(word: u32) -> vec4<f32> { return vec4<f32>(v3(word), f(word + 3u)); }

fn loadJob(index: u32) -> DeformationJob {
    // Offsets retain the complete canonical 112-byte job layout, including its
    // handles and generation fields that the deformation arithmetic does not use.
    let word = address(0u, index);
    return DeformationJob(
        data[word + 4u], data[word + 5u], data[word + 7u], data[word + 8u],
        data[word + 9u], data[word + 10u], data[word + 11u], data[word + 12u],
        data[word + 13u], data[word + 16u], data[word + 17u], data[word + 24u],
        data[word + 26u], data[word + 27u]);
}

fn resolveGroupedJob(vertex: u32) -> u32 {
    var low = parameters.firstGroupedJob;
    var high = low + parameters.groupedJobCount;
    while (low < high) {
        let middle = low + (high - low) / 2u;
        let first = data[address(2u, middle)];
        let jobIndex = data[address(1u, middle)];
        if (jobIndex >= sectionCount(0u)) { return INVALID; }
        let count = data[address(0u, jobIndex) + 13u];
        if (vertex < first) { high = middle; }
        else if (vertex - first >= count) { low = middle + 1u; }
        else { return middle; }
    }
    return INVALID;
}

fn unitOrZero(value: vec3<f32>) -> vec3<f32> {
    let lengthSquared = dot(value, value);
    if (lengthSquared <= 1e-20) { return vec3<f32>(0.0); }
    return value * inverseSqrt(lengthSquared);
}

fn signNotZero(value: vec2<f32>) -> vec2<f32> {
    return select(vec2<f32>(-1.0), vec2<f32>(1.0), value >= vec2<f32>(0.0));
}

fn decodeOctValue(encoded: vec2<f32>) -> vec3<f32> {
    var value = vec3<f32>(encoded, 1.0 - abs(encoded.x) - abs(encoded.y));
    if (value.z < 0.0) {
        let folded = (vec2<f32>(1.0) - abs(value.yx)) * signNotZero(value.xy);
        value = vec3<f32>(folded, value.z);
    }
    return unitOrZero(value);
}

fn encodeOctValue(value: vec3<f32>) -> vec2<f32> {
    let magnitude = abs(value.x) + abs(value.y) + abs(value.z);
    if (magnitude <= 1e-20) { return vec2<f32>(0.0); }
    let unit = value / magnitude;
    if (unit.z >= 0.0) { return unit.xy; }
    return (vec2<f32>(1.0) - abs(unit.yx)) * signNotZero(unit.xy);
}

fn decodeTangent(packedValue: u32) -> vec3<f32> {
    let x = bitcast<i32>(packedValue << 16u) >> 16u;
    let y = bitcast<i32>((packedValue >> 16u) << 17u) >> 17u;
    return decodeOctValue(vec2<f32>(f32(x) / 32767.0, f32(y) / 16383.0));
}

fn encodeTangent(value: vec3<f32>, signBit: u32) -> u32 {
    let encoded = clamp(encodeOctValue(value), vec2<f32>(-1.0), vec2<f32>(1.0));
    let x = bitcast<u32>(i32(round(encoded.x * 32767.0))) & 0xffffu;
    let y = bitcast<u32>(i32(round(encoded.y * 16383.0))) & 0x7fffu;
    return x | (y << 16u) | signBit;
}

fn transformRows(word: u32, source: Deformed) -> Deformed {
    let a = v4(word);
    let b = v4(word + 4u);
    let c = v4(word + 8u);
    let point = vec4<f32>(source.position, 1.0);
    // The authored generated vertex/prepass contract transforms both N and T
    // with cofactors. Keep the existing packed-only calculation unchanged.
    let ca = cross(b.xyz, c.xyz);
    let cb = cross(c.xyz, a.xyz);
    let cc = cross(a.xyz, b.xyz);
    return Deformed(
        vec3<f32>(dot(a, point), dot(b, point), dot(c, point)),
        vec3<f32>(dot(a.xyz, source.normal), dot(b.xyz, source.normal), dot(c.xyz, source.normal)),
        vec3<f32>(dot(a.xyz, source.tangent), dot(b.xyz, source.tangent), dot(c.xyz, source.tangent)),
        vec3<f32>(dot(ca, source.authoredNormal), dot(cb, source.authoredNormal), dot(cc, source.authoredNormal)),
        vec3<f32>(dot(ca, source.authoredTangent), dot(cb, source.authoredTangent), dot(cc, source.authoredTangent)));
}

fn addInfluence(weighted: Weighted, source: Deformed, job: DeformationJob, bone: u32, weight: f32) -> Weighted {
    if (weight <= 0.0 || bone >= job.boneCount) { return weighted; }
    var bound = source;
    if ((job.features & FEATURE_PRECOMPOSED_PALETTE) == 0u) {
        bound = transformRows(address(6u, job.inverseBindOffset + bone), bound);
    }
    let transformed = transformRows(address(5u, job.bonePaletteOffset + bone), bound);
    var result = weighted;
    result.value.position += transformed.position * weight;
    result.value.normal += transformed.normal * weight;
    result.value.tangent += transformed.tangent * weight;
    result.value.authoredNormal += transformed.authoredNormal * weight;
    result.value.authoredTangent += transformed.authoredTangent * weight;
    result.total += weight;
    return result;
}

fn applySparseBlendshapes(source: Deformed, job: DeformationJob, vertex: u32, threshold: f32) -> MorphResult {
    if ((job.features & FEATURE_BLENDSHAPES) == 0u) { return MorphResult(source, true); }
    if (!rangeValid(7u, job.blendshapeWeightOffset, job.blendshapeCount)) { return MorphResult(source, false); }
    var accumulated = Deformed(vec3<f32>(0.0), vec3<f32>(0.0), vec3<f32>(0.0), vec3<f32>(0.0), vec3<f32>(0.0));
    for (var activeIndex = 0u; activeIndex < job.blendshapeCount; activeIndex++) {
        let active = address(7u, job.blendshapeWeightOffset + activeIndex);
        let weight = f(active + 1u);
        if (abs(weight) <= threshold) { continue; }
        let shape = data[active];
        if (job.blendshapeShapeOffset >= sectionCount(10u) || shape >= sectionCount(10u) - job.blendshapeShapeOffset) {
            return MorphResult(source, false);
        }
        let range = address(10u, job.blendshapeShapeOffset + shape);
        let first = data[range];
        let count = data[range + 1u];
        if (!rangeValid(11u, first, count)) { return MorphResult(source, false); }
        var low = first;
        var high = first + count;
        var found = INVALID;
        while (low < high) {
            let middle = low + (high - low) / 2u;
            let candidate = data[address(11u, middle)];
            if (candidate < vertex) { low = middle + 1u; }
            else if (candidate > vertex) { high = middle; }
            else { found = middle; break; }
        }
        if (found == INVALID) { continue; }
        let record = address(11u, found);
        let positionDelta = data[record + 1u];
        let normalDelta = data[record + 2u];
        let tangentDelta = data[record + 3u];
        if (positionDelta >= sectionCount(8u) || normalDelta >= sectionCount(8u) || tangentDelta >= sectionCount(8u)) {
            return MorphResult(source, false);
        }
        let p = v3(address(8u, positionDelta)) * weight;
        let n = v3(address(8u, normalDelta)) * weight;
        let t = v3(address(8u, tangentDelta)) * weight;
        if ((job.features & FEATURE_MAXIMUM_BLENDSHAPE) != 0u) {
            accumulated.position = max(accumulated.position, p);
            accumulated.normal = max(accumulated.normal, n);
            accumulated.tangent = max(accumulated.tangent, t);
        } else {
            accumulated.position += p;
            accumulated.normal += n;
            accumulated.tangent += t;
        }
    }
    var result = source;
    result.position += accumulated.position;
    result.normal += accumulated.normal;
    result.tangent += accumulated.tangent;
    if ((job.features & FEATURE_NORMALS) != 0u) { result.authoredNormal += accumulated.normal; }
    if ((job.features & FEATURE_TANGENTS) != 0u) { result.authoredTangent += accumulated.tangent; }
    return MorphResult(result, true);
}

fn authoredLengthValid(value: vec3<f32>) -> bool {
    let lengthSquared = dot(value, value);
    return lengthSquared > 0.0 && lengthSquared <= 3.402823466e+38;
}

fn writeAuthoredBasis(vertex: u32, sourceWord: u32, result: Deformed) {
    let flags = data[sourceWord + 3u] & 1u;
    let sign = f(sourceWord + 7u);
    // Preserve the prepass's normalization, including invalid zero vectors,
    // without introducing the canonical codec's unit-axis repairs.
    var normal = vec3<f32>(0.0);
    var tangent = vec3<f32>(0.0);
    var outputFlags = flags;
    if (authoredLengthValid(result.authoredNormal)) {
        normal = normalize(result.authoredNormal);
        outputFlags |= 2u;
    }
    if (authoredLengthValid(result.authoredTangent)) { tangent = normalize(result.authoredTangent); }
    if ((outputFlags & 3u) == 3u && authoredLengthValid(tangent) && authoredLengthValid(cross(normal, tangent) * sign)) {
        outputFlags |= 4u;
    }
    let output = vertex * 8u;
    authoredBasis[output] = bitcast<u32>(normal.x);
    authoredBasis[output + 1u] = bitcast<u32>(normal.y);
    authoredBasis[output + 2u] = bitcast<u32>(normal.z);
    authoredBasis[output + 3u] = outputFlags;
    authoredBasis[output + 4u] = bitcast<u32>(tangent.x);
    authoredBasis[output + 5u] = bitcast<u32>(tangent.y);
    authoredBasis[output + 6u] = bitcast<u32>(tangent.z);
    authoredBasis[output + 7u] = bitcast<u32>(sign);
}

@compute @workgroup_size(256, 1, 1)
fn advancedAggregateDeformation(@builtin(global_invocation_id) invocation: vec3<u32>) {
    let vertex = invocation.x;
    if (vertex >= parameters.batchVertexCount || !sectionsValid()) { return; }
    if (!rangeValid(1u, parameters.firstGroupedJob, parameters.groupedJobCount) ||
        !rangeValid(2u, parameters.firstGroupedJob, parameters.groupedJobCount)) { return; }
    let groupedJob = resolveGroupedJob(vertex);
    if (groupedJob == INVALID) { return; }
    let jobIndex = data[address(1u, groupedJob)];
    if (jobIndex >= sectionCount(12u)) { return; }
    let job = loadJob(jobIndex);
    let localVertex = vertex - data[address(2u, groupedJob)];
    let outputCount = arrayLength(&current) / 16u;
    if (job.order != 0u || job.outputStride != 64u ||
        job.sourceVertexOffset > sectionCount(3u) || job.vertexFirst > sectionCount(3u) - job.sourceVertexOffset ||
        job.vertexCount > sectionCount(3u) - job.sourceVertexOffset - job.vertexFirst ||
        job.currentVertexOffset > outputCount || job.vertexCount > outputCount - job.currentVertexOffset ||
        !rangeValid(13u, job.sourceVertexOffset + job.vertexFirst, job.vertexCount) ||
        job.currentVertexOffset > arrayLength(&authoredBasis) / 8u ||
        job.vertexCount > arrayLength(&authoredBasis) / 8u - job.currentVertexOffset) { return; }
    let sourceIndex = job.sourceVertexOffset + job.vertexFirst + localVertex;
    let sourceWord = address(3u, sourceIndex);
    let controls = address(12u, jobIndex);
    let cap = data[controls];
    let basisWord = address(13u, sourceIndex);
    var source = Deformed(v3(sourceWord), decodeOctValue(unpack2x16snorm(data[sourceWord + 3u])),
        decodeTangent(data[sourceWord + 4u]), v3(basisWord), v3(basisWord + 4u));
    // Authored blendshapes modify bind-space attributes before skinning.
    let morphed = applySparseBlendshapes(source, job, localVertex, f(controls + 1u));
    if (!morphed.valid) { return; }
    source = morphed.value;
    var result = source;
    if ((job.features & FEATURE_SKINNING) != 0u) {
        if (!rangeValid(4u, job.boneInfluenceOffset, job.vertexCount) ||
            !rangeValid(5u, job.bonePaletteOffset, job.boneCount)) { return; }
        if ((job.features & FEATURE_PRECOMPOSED_PALETTE) == 0u && !rangeValid(6u, job.inverseBindOffset, job.boneCount)) { return; }
        let influence = address(4u, job.boneInfluenceOffset + localVertex);
        var weighted = Weighted(Deformed(vec3<f32>(0.0), vec3<f32>(0.0), vec3<f32>(0.0), vec3<f32>(0.0), vec3<f32>(0.0)), 0.0);
        for (var lane = 0u; lane < min(4u, cap); lane++) {
            weighted = addInfluence(weighted, source, job, data[influence + lane], f(influence + 4u + lane));
        }
        if ((job.features & FEATURE_SPILL_INFLUENCES) != 0u && cap > 4u) {
            let spillFirst = data[influence + 8u];
            let spillCount = min(data[influence + 9u], cap - 4u);
            if (!rangeValid(9u, spillFirst, spillCount)) { return; }
            for (var lane = 0u; lane < spillCount; lane++) {
                let spill = address(9u, spillFirst + lane);
                weighted = addInfluence(weighted, source, job, data[spill], f(spill + 1u));
            }
        }
        if (weighted.total > 0.0) {
            result.position = weighted.value.position / weighted.total;
            result.normal = unitOrZero(weighted.value.normal);
            result.tangent = unitOrZero(weighted.value.tangent);
        }
        if (weighted.total > 0.0001) {
            result.authoredNormal = weighted.value.authoredNormal;
            result.authoredTangent = weighted.value.authoredTangent;
        }
    }
    // Copy the complete canonical 64-byte vertex before replacing deformation
    // attributes. UVs, colors, flags, custom values and reserved words survive.
    let destination = (job.currentVertexOffset + localVertex) * 16u;
    for (var word = 0u; word < 16u; word++) { current[destination + word] = data[sourceWord + word]; }
    current[destination] = bitcast<u32>(result.position.x);
    current[destination + 1u] = bitcast<u32>(result.position.y);
    current[destination + 2u] = bitcast<u32>(result.position.z);
    if ((job.features & FEATURE_NORMALS) != 0u) { current[destination + 3u] = pack2x16snorm(encodeOctValue(result.normal)); }
    if ((job.features & FEATURE_TANGENTS) != 0u) {
        current[destination + 4u] = encodeTangent(result.tangent, data[sourceWord + 4u] & 0x80000000u);
    }
    current[destination + 10u] = sourceIndex;
    writeAuthoredBasis(job.currentVertexOffset + localVertex, basisWord, result);
}
