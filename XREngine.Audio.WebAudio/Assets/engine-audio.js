const contexts = new Map();
let nextContext = 0;
let audioGeneration = 0;
const maxBuffers = 32;
const maxSources = 32;
const maxPcmBytes = 8 * 1024 * 1024;

function requireContext(id) {
    const owner = contexts.get(id);
    if (!owner) throw new Error('WebAudio.StaleContext: output owner has closed.');
    return owner;
}
function requireSource(owner, id) {
    const source = owner.sources.get(id);
    if (!source) throw new Error('WebAudio.InvalidSource: source handle is not owned by this output.');
    return source;
}
function requireBuffer(owner, id) {
    const buffer = owner.buffers.get(id);
    if (!buffer) throw new Error('WebAudio.InvalidBuffer: buffer handle is not owned by this output.');
    return buffer;
}
function finite(value, label) {
    if (!Number.isFinite(value)) throw new Error(`WebAudio.Invalid${label}: value must be finite.`);
    return value;
}
function position(param, value) {
    param.value = finite(value, 'Position');
}
function playbackOffset(owner, source) {
    if (!source.playing || !source.buffer) return source.offset;
    const elapsed = (owner.context.currentTime - source.started) * source.rate;
    const duration = source.buffer.duration;
    return source.loop ? (source.offset + elapsed) % duration : Math.min(duration, source.offset + elapsed);
}
function applyRate(owner, source) {
    const dx = source.position[0] - owner.listenerPosition[0];
    const dy = source.position[1] - owner.listenerPosition[1];
    const dz = source.position[2] - owner.listenerPosition[2];
    const distance = Math.hypot(dx, dy, dz);
    let doppler = 1;
    if (distance > 0.001) {
        const nx = dx / distance, ny = dy / distance, nz = dz / distance;
        const towardSource = owner.listenerVelocity[0] * nx + owner.listenerVelocity[1] * ny
            + owner.listenerVelocity[2] * nz;
        const awayFromListener = source.velocity[0] * nx + source.velocity[1] * ny
            + source.velocity[2] * nz;
        doppler = Math.max(0.5, Math.min(2,
            (343.3 + towardSource) / Math.max(34.33, 343.3 + awayFromListener)));
    }
    source.offset = playbackOffset(owner, source);
    source.started = owner.context.currentTime;
    source.rate = Math.max(0.01, Math.min(4, source.pitch * doppler));
    if (source.node) source.node.playbackRate.value = source.rate;
}
function detachNode(source) {
    if (!source.node) return;
    const node = source.node;
    source.node = null;
    node.onended = null;
    try { node.stop(); } catch { /* Already ended. */ }
    node.disconnect();
}
function createSource(owner) {
    if (owner.sources.size >= maxSources) throw new Error('WebAudio.SourceCapacityExceeded.');
    const id = ++owner.nextSource;
    const panner = owner.context.createPanner();
    panner.panningModel = 'HRTF';
    panner.distanceModel = 'inverse';
    const gain = owner.context.createGain();
    panner.connect(gain);
    gain.connect(owner.master);
    const source = { bufferId: 0, buffer: null, panner, gain, node: null,
        offset: 0, started: 0, pitch: 1, rate: 1, loop: false, playing: false,
        position: [0, 0, 0], velocity: [0, 0, 0] };
    owner.sources.set(id, source);
    owner.sourceList.push(source);
    return id;
}

/** A browser output remains suspended until unlock is invoked from a user gesture. */
export const engineAudioImports = {
    open() {
        const AudioContextType = globalThis.AudioContext ?? globalThis.webkitAudioContext;
        if (!AudioContextType) throw new Error('WebAudio.Unavailable: this browser has no AudioContext.');
        const context = new AudioContextType({ latencyHint: 'interactive' });
        const master = context.createGain();
        master.connect(context.destination);
        const id = ++nextContext;
        contexts.set(id, { context, master, buffers: new Map(), sources: new Map(), sourceList: [], nextBuffer: 0, nextSource: 0,
            pcmBytes: 0, listenerPosition: [0, 0, 0], listenerVelocity: [0, 0, 0] });
        audioGeneration++;
        return id;
    },
    close(id) {
        const owner = contexts.get(id);
        if (!owner) return;
        contexts.delete(id);
        audioGeneration++;
        for (const source of owner.sources.values()) {
            detachNode(source);
            source.panner.disconnect();
            source.gain.disconnect();
        }
        owner.sources.clear();
        owner.sourceList.length = 0;
        owner.buffers.clear();
        owner.master.disconnect();
        void owner.context.close().catch(() => {});
    },
    sampleRate: id => requireContext(id).context.sampleRate,
    isOpen: id => requireContext(id).context.state !== 'closed',
    unlock() {
        if (!contexts.size) return Promise.resolve(false);
        const generation = audioGeneration;
        // Issue every resume synchronously while the trusted gesture is still active.
        const resumes = Array.from(contexts.values(), owner => owner.context.resume());
        return Promise.all(resumes).then(() => generation === audioGeneration && contexts.size > 0 &&
            Array.from(contexts.values()).every(owner => owner.context.state === 'running'));
    },
    state() {
        if (!contexts.size) return 'pending';
        return Array.from(contexts.values()).every(owner => owner.context.state === 'running')
            ? 'ready' : 'suspended';
    },
    listenerPosition(id, x, y, z) {
        const owner = requireContext(id), listener = owner.context.listener;
        position(listener.positionX, x); position(listener.positionY, y); position(listener.positionZ, z);
        owner.listenerPosition[0] = x; owner.listenerPosition[1] = y; owner.listenerPosition[2] = z;
        for (let index = 0; index < owner.sourceList.length; index++) applyRate(owner, owner.sourceList[index]);
    },
    listenerVelocity(id, x, y, z) {
        const owner = requireContext(id);
        owner.listenerVelocity[0] = finite(x, 'Velocity');
        owner.listenerVelocity[1] = finite(y, 'Velocity');
        owner.listenerVelocity[2] = finite(z, 'Velocity');
        for (let index = 0; index < owner.sourceList.length; index++) applyRate(owner, owner.sourceList[index]);
    },
    listenerOrientation(id, fx, fy, fz, ux, uy, uz) {
        const listener = requireContext(id).context.listener;
        position(listener.forwardX, fx); position(listener.forwardY, fy); position(listener.forwardZ, fz);
        position(listener.upX, ux); position(listener.upY, uy); position(listener.upZ, uz);
    },
    listenerGain(id, gain) { requireContext(id).master.gain.value = Math.max(0, finite(gain, 'Gain')); },
    createSource(id) { return createSource(requireContext(id)); },
    destroySource(id, sourceId) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        detachNode(source); source.panner.disconnect(); source.gain.disconnect(); owner.sources.delete(sourceId);
        owner.sourceList.splice(owner.sourceList.indexOf(source), 1);
    },
    createBuffer(id) {
        const owner = requireContext(id);
        if (owner.buffers.size >= maxBuffers) throw new Error('WebAudio.BufferCapacityExceeded.');
        const bufferId = ++owner.nextBuffer;
        owner.buffers.set(bufferId, { audio: null, bytes: 0 });
        return bufferId;
    },
    destroyBuffer(id, bufferId) {
        const owner = requireContext(id), entry = requireBuffer(owner, bufferId);
        if (Array.from(owner.sources.values()).some(source => source.bufferId === bufferId))
            throw new Error('WebAudio.BufferInUse: detach sources before destroying this buffer.');
        owner.pcmBytes -= entry.bytes;
        owner.buffers.delete(bufferId);
    },
    uploadBuffer(id, bufferId, pcm, frequency, channels, format) {
        const owner = requireContext(id), entry = requireBuffer(owner, bufferId);
        if (!Number.isInteger(frequency) || frequency < 8000 || frequency > 192000
            || ![1, 2].includes(channels) || ![0, 1, 2].includes(format))
            throw new Error('WebAudio.PcmFormatUnsupported: only mono/stereo 8-bit, 16-bit or float PCM is supported.');
        const bytesPerSample = format === 0 ? 1 : format === 1 ? 2 : 4;
        const bytes = pcm.slice();
        if (!bytes.byteLength || bytes.byteLength % (channels * bytesPerSample)
            || owner.pcmBytes - entry.bytes + bytes.byteLength > maxPcmBytes)
            throw new Error('WebAudio.PcmBudgetExceeded: invalid or oversized PCM payload.');
        const frames = bytes.byteLength / (channels * bytesPerSample);
        const audio = owner.context.createBuffer(channels, frames, frequency);
        const data = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
        for (let channel = 0; channel < channels; channel++) {
            const samples = audio.getChannelData(channel);
            for (let frame = 0; frame < frames; frame++) {
                const offset = (frame * channels + channel) * bytesPerSample;
                const sample = format === 0 ? (data.getUint8(offset) - 128) / 128
                    : format === 1 ? data.getInt16(offset, true) / 32768
                    : data.getFloat32(offset, true);
                if (!Number.isFinite(sample)) throw new Error('WebAudio.PcmInvalid: samples must be finite.');
                samples[frame] = sample;
            }
        }
        owner.pcmBytes += bytes.byteLength - entry.bytes;
        entry.audio = audio;
        entry.bytes = bytes.byteLength;
    },
    play(id, sourceId) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        if (!source.buffer) throw new Error('WebAudio.BufferMissing: attach PCM before playback.');
        if (source.playing) return;
        if (source.offset >= source.buffer.duration) source.offset = 0;
        const node = owner.context.createBufferSource();
        node.buffer = source.buffer;
        node.loop = source.loop;
        node.playbackRate.value = source.rate;
        node.connect(source.panner);
        source.node = node;
        source.started = owner.context.currentTime;
        source.playing = true;
        node.onended = () => {
            if (source.node !== node) return;
            source.node = null;
            source.playing = false;
            source.offset = 0;
            node.disconnect();
        };
        node.start(0, source.offset);
    },
    stop(id, sourceId) {
        const source = requireSource(requireContext(id), sourceId);
        detachNode(source); source.playing = false; source.offset = 0;
    },
    pause(id, sourceId) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        source.offset = playbackOffset(owner, source);
        detachNode(source); source.playing = false;
    },
    rewind(id, sourceId) {
        const source = requireSource(requireContext(id), sourceId);
        detachNode(source); source.playing = false; source.offset = 0;
    },
    setSourceBuffer(id, sourceId, bufferId) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        const buffer = bufferId ? requireBuffer(owner, bufferId).audio : null;
        if (bufferId && !buffer) throw new Error('WebAudio.BufferEmpty: upload PCM before attaching.');
        detachNode(source); source.playing = false; source.offset = 0;
        source.bufferId = bufferId; source.buffer = buffer;
    },
    sourcePosition(id, sourceId, x, y, z) {
        const owner = requireContext(id), source = requireSource(owner, sourceId), panner = source.panner;
        position(panner.positionX, x); position(panner.positionY, y); position(panner.positionZ, z);
        source.position[0] = x; source.position[1] = y; source.position[2] = z;
        applyRate(owner, source);
    },
    sourceVelocity(id, sourceId, x, y, z) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        source.velocity[0] = finite(x, 'Velocity');
        source.velocity[1] = finite(y, 'Velocity');
        source.velocity[2] = finite(z, 'Velocity');
        applyRate(owner, source);
    },
    sourceGain(id, sourceId, gain) {
        requireSource(requireContext(id), sourceId).gain.gain.value = Math.max(0, finite(gain, 'Gain'));
    },
    sourcePitch(id, sourceId, pitch) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        if (!Number.isFinite(pitch) || pitch <= 0 || pitch > 4)
            throw new Error('WebAudio.PitchOutOfRange: playback rate must be within (0, 4].');
        source.pitch = pitch;
        applyRate(owner, source);
    },
    sourceLooping(id, sourceId, loop) {
        const source = requireSource(requireContext(id), sourceId);
        source.loop = Boolean(loop);
        if (source.node) source.node.loop = source.loop;
    },
    isSourcePlaying(id, sourceId) { return requireSource(requireContext(id), sourceId).playing; },
    sampleOffset(id, sourceId) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        return source.buffer ? Math.floor(playbackOffset(owner, source) * source.buffer.sampleRate) : 0;
    },
};
