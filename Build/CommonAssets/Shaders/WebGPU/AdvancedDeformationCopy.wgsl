struct Parameters {
    currentWordCount: u32,
    previousWordCount: u32,
    reserved0: u32,
    reserved1: u32,
};

@group(0) @binding(0) var<storage, read> current: array<u32>;
@group(0) @binding(1) var<storage, read> previous: array<u32>;
@group(0) @binding(2) var<storage, read_write> geometry: array<u32>;
@group(0) @binding(3) var<storage, read> currentBasis: array<u32>;
@group(0) @binding(4) var<storage, read> previousBasis: array<u32>;
@group(0) @binding(5) var<uniform> parameters: Parameters;

// Canonical geometry has seven four-word directory rows. Offsets are words;
// lengths are exact bytes, independent of the backing buffer's capacity.
fn destinationWord(stream: u32, word: u32, basis: bool) -> u32 {
    let length = arrayLength(&geometry);
    if (length < 28u) { return length; }
    let directory = stream * 4u + select(0u, 2u, basis);
    let offset = geometry[directory];
    let byteLength = geometry[directory + 1u];
    if ((byteLength & 3u) != 0u || offset < 28u || offset > length) { return length; }
    let wordCount = byteLength / 4u;
    if (wordCount > length - offset || word >= wordCount) { return length; }
    return offset + word;
}

fn copyRangeValid(stream: u32, words: u32, sourceWords: u32, basisWords: u32) -> bool {
    if ((words & 15u) != 0u || words > sourceWords || words / 2u > basisWords) { return false; }
    if (words == 0u) { return true; }
    let geometryWords = arrayLength(&geometry);
    return destinationWord(stream, words - 1u, false) < geometryWords &&
        destinationWord(stream, words / 2u - 1u, true) < geometryWords;
}

@compute @workgroup_size(256, 1, 1)
fn advancedDeformationCopy(@builtin(global_invocation_id) invocation: vec3<u32>, @builtin(num_workgroups) groups: vec3<u32>) {
    if (!copyRangeValid(2u, parameters.currentWordCount, arrayLength(&current), arrayLength(&currentBasis)) ||
        !copyRangeValid(3u, parameters.previousWordCount, arrayLength(&previous), arrayLength(&previousBasis))) { return; }
    let word = invocation.x + invocation.y * groups.x * 256u;
    if (word < parameters.currentWordCount && word < arrayLength(&current)) {
        let destination = destinationWord(2u, word, false);
        if (destination < arrayLength(&geometry)) { geometry[destination] = current[word]; }
    }
    if (word < parameters.previousWordCount && word < arrayLength(&previous)) {
        let destination = destinationWord(3u, word, false);
        if (destination < arrayLength(&geometry)) { geometry[destination] = previous[word]; }
    }
    if (word < parameters.currentWordCount / 2u) {
        geometry[destinationWord(2u, word, true)] = currentBasis[word];
    }
    if (word < parameters.previousWordCount / 2u) {
        geometry[destinationWord(3u, word, true)] = previousBasis[word];
    }
}
