struct Parameters {
    currentWordCount: u32,
    previousWordCount: u32,
    reserved0: u32,
    reserved1: u32,
};

@group(0) @binding(0) var<storage, read> current: array<u32>;
@group(0) @binding(1) var<storage, read> previous: array<u32>;
@group(0) @binding(2) var<storage, read_write> geometry: array<u32>;
@group(0) @binding(3) var<uniform> parameters: Parameters;

// Canonical geometry has seven four-word directory rows. Offsets are words;
// lengths are exact bytes, independent of the backing buffer's capacity.
fn destinationWord(stream: u32, word: u32) -> u32 {
    let length = arrayLength(&geometry);
    if (length < 28u) { return length; }
    let directory = stream * 4u;
    let offset = geometry[directory];
    let byteLength = geometry[directory + 1u];
    if ((byteLength & 3u) != 0u || offset < 28u || offset > length) { return length; }
    let wordCount = byteLength / 4u;
    if (wordCount > length - offset || word >= wordCount) { return length; }
    return offset + word;
}

@compute @workgroup_size(256, 1, 1)
fn advancedDeformationCopy(@builtin(global_invocation_id) invocation: vec3<u32>, @builtin(num_workgroups) groups: vec3<u32>) {
    let word = invocation.x + invocation.y * groups.x * 256u;
    if (word < parameters.currentWordCount && word < arrayLength(&current)) {
        let destination = destinationWord(2u, word);
        if (destination < arrayLength(&geometry)) { geometry[destination] = current[word]; }
    }
    if (word < parameters.previousWordCount && word < arrayLength(&previous)) {
        let destination = destinationWord(3u, word);
        if (destination < arrayLength(&geometry)) { geometry[destination] = previous[word]; }
    }
}
