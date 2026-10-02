import { WebAudioStream } from './web-audio-stream.js';
import { WebAudioActivation } from './web-audio-activation.js';
import { setListenerBasis, updateSpatialSource, spatialPlaybackRate, validateDistanceModel } from './web-audio-spatial.js';

const contexts = new Map();
let nextContext = 0;
let nextSource = 0;
let nextBuffer = 0;
const contextOwners = [];
const maxBuffers = 32;
const maxSources = 32;
const maxPcmBytes = 8 * 1024 * 1024;
const activation = new WebAudioActivation(contexts, contextOwners, activateSources);
let spatialBatch = 0;
let nextSpatialBatch = 0;

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
    const rate = spatialPlaybackRate(owner, source);
    if (rate === source.rate) return;
    source.offset = playbackOffset(owner, source);
    source.started = owner.context.currentTime;
    source.rate = rate;
    if (source.node) source.node.playbackRate.value = rate;
    source.stream.setRate(rate);
}
function flushListenerSpatial(owner) {
    if (owner.appliedSpatialVersion === owner.spatialVersion) return;
    const listener = owner.context.listener;
    position(listener.positionX, owner.listenerPosition[0]); position(listener.positionY, owner.listenerPosition[1]); position(listener.positionZ, owner.listenerPosition[2]);
    position(listener.forwardX, owner.listenerForward[0]); position(listener.forwardY, owner.listenerForward[1]); position(listener.forwardZ, owner.listenerForward[2]);
    position(listener.upX, owner.listenerUp[0]); position(listener.upY, owner.listenerUp[1]); position(listener.upZ, owner.listenerUp[2]);
    owner.appliedSpatialVersion = owner.spatialVersion;
}
function flushSourceSpatial(owner, source) {
    flushListenerSpatial(owner);
    if (!source.spatialDirty && source.appliedSpatialVersion === owner.spatialVersion) return;
    updateSpatialSource(owner, source);
    applyRate(owner, source);
    source.spatialDirty = false;
    source.appliedSpatialVersion = owner.spatialVersion;
}
function flushOwnerSpatial(owner) {
    flushListenerSpatial(owner);
    for (let index = 0; index < owner.sourceList.length; index++) flushSourceSpatial(owner, owner.sourceList[index]);
}
function markSourceSpatial(owner, source) {
    source.spatialDirty = true;
    if (!spatialBatch) flushSourceSpatial(owner, source);
}
function markListenerSpatial(owner) {
    owner.spatialVersion++;
    if (!spatialBatch) flushOwnerSpatial(owner);
}
function activateSources(owner) {
    for (let index = 0; index < owner.sourceList.length; index++) {
        const source = owner.sourceList[index];
        if (source.deferredPlayback) engineAudioImports.play(owner.id, source.id);
    }
}
function nonnegative(value, label, allowInfinity = false) {
    if ((allowInfinity && value === Infinity) || Number.isFinite(value) && value >= 0) return value;
    throw new Error(`WebAudio.Invalid${label}: value must be nonnegative and finite.`);
}
function bounded(value, maximum, label) {
    if (!Number.isFinite(value) || value < 0 || value > maximum)
        throw new Error(`WebAudio.Invalid${label}: value must be within [0, ${maximum}].`);
    return value;
}
function queueSeconds(owner, source) {
    return source.stream.count ? source.stream.queueOffset() : playbackOffset(owner, source);
}
function queueAudio(source) { return source.stream.count ? source.stream.entry(0).audio : source.buffer; }
function offsetValue(owner, source, unit) {
    const audio = queueAudio(source);
    if (!audio) return 0;
    const seconds = queueSeconds(owner, source);
    if (unit === 0) return seconds;
    if (unit === 1) return Math.floor(seconds * audio.sampleRate);
    if (unit === 2) return Math.floor(seconds * audio.sampleRate) * source.bytesPerFrame;
    throw new Error('WebAudio.OffsetUnitUnsupported.');
}
function bufferInUse(owner, id) {
    for (let index = 0; index < owner.sourceList.length; index++) {
        const source = owner.sourceList[index];
        if (source.bufferId === id || source.stream.contains(id)) return true;
    }
    return false;
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
    if (nextSource >= 0x7fffffff) throw new Error('WebAudio.SourceHandleExhausted.');
    const id = ++nextSource;
    const panner = owner.context.createPanner();
    panner.panningModel = 'HRTF';
    panner.distanceModel = 'inverse';
    panner.rolloffFactor = 0;
    panner.coneInnerAngle = panner.coneOuterAngle = 360;
    const gain = owner.context.createGain();
    panner.connect(gain);
    gain.connect(owner.master);
    const source = { id, bufferId: 0, buffer: null, panner, gain, node: null,
        offset: 0, started: 0, pitch: 1, rate: 1, loop: false, playing: false,
        state: 'initial', deferredPlayback: false, relative: false, channels: 1, bytesPerFrame: 0,
        spatialDirty: true, appliedSpatialVersion: -1,
        gainValue: 1, minGain: 0, maxGain: 1, referenceDistance: 1, maxDistance: Infinity, rolloff: 1,
        coneInner: 360, coneOuter: 360, coneOuterGain: 0,
        position: [0, 0, 0], velocity: [0, 0, 0], direction: [0, 0, 0],
        worldPosition: [0, 0, 0], worldDirection: [0, 0, 0],
        stream: new WebAudioStream(owner.context, panner), transfer: new Int32Array(32) };
    owner.sources.set(id, source);
    owner.sourceList.push(source);
    flushSourceSpatial(owner, source);
    return id;
}

/** A browser output remains suspended until unlock is invoked from a user gesture. */
export const engineAudioImports = {
    open() {
        const AudioContextType = globalThis.AudioContext ?? globalThis.webkitAudioContext;
        if (!AudioContextType) throw new Error('WebAudio.Unavailable: this browser has no AudioContext.');
        const context = new AudioContextType({ latencyHint: 'interactive' });
        const master = context.createGain();
        master.gain.value = 0;
        master.connect(context.destination);
        const id = ++nextContext;
        const owner = { id, context, master, buffers: new Map(), sources: new Map(), sourceList: [],
            pcmBytes: 0, listenerGain: 1, dopplerFactor: 1, speedOfSound: 343.3, distanceModel: 0xD002,
            activated: false, activationFailure: '', spatialVersion: 0, appliedSpatialVersion: -1, listenerPosition: [0, 0, 0], listenerVelocity: [0, 0, 0],
            listenerForward: [0, 0, -1], listenerUp: [0, 1, 0], listenerRight: [1, 0, 0] };
        contexts.set(id, owner);
        contextOwners.push(owner);
        activation.opened(owner);
        return id;
    },
    close(id) {
        const owner = contexts.get(id);
        if (!owner) return;
        contexts.delete(id);
        contextOwners.splice(contextOwners.indexOf(owner), 1);
        activation.closed(owner);
        for (const source of owner.sources.values()) {
            source.stream.dispose();
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
    unlock: () => activation.unlock(),
    state: () => activation.state,
    isReady: () => activation.ready,
    hasActivationFailure: () => Boolean(activation.failure),
    activationFailure: () => activation.failure,
    setPageActive: active => activation.setPageActive(active),
    setSurfaceActive: active => activation.setSurfaceActive(active),
    beginSpatialUpdates() {
        if (spatialBatch) throw new Error('WebAudio.SpatialBatchAlreadyActive: audio frames cannot nest.');
        nextSpatialBatch = nextSpatialBatch === 0x7fffffff ? 1 : nextSpatialBatch + 1;
        spatialBatch = nextSpatialBatch;
        return spatialBatch;
    },
    endSpatialUpdates(batch) {
        if (!spatialBatch || batch !== spatialBatch) throw new Error('WebAudio.SpatialBatchStale: only the active frame can flush its updates.');
        spatialBatch = 0;
        for (let index = 0; index < contextOwners.length; index++) flushOwnerSpatial(contextOwners[index]);
    },
    listenerProperty(id, property) {
        const owner = requireContext(id);
        if (property === 0) return owner.dopplerFactor;
        if (property === 1) return owner.speedOfSound;
        if (property === 2) return owner.distanceModel;
        throw new Error('WebAudio.ListenerPropertyUnsupported.');
    },
    setListenerProperty(id, property, value) {
        const owner = requireContext(id);
        if (property === 0) owner.dopplerFactor = nonnegative(value, 'DopplerFactor');
        else if (property === 1) {
            if (!Number.isFinite(value) || value <= 0) throw new Error('WebAudio.InvalidSpeedOfSound: value must be positive and finite.');
            owner.speedOfSound = value;
        } else if (property === 2) { validateDistanceModel(value); owner.distanceModel = value; }
        else throw new Error('WebAudio.ListenerPropertyUnsupported.');
        markListenerSpatial(owner);
    },
    listenerPosition(id, x, y, z) {
        const owner = requireContext(id);
        finite(x, 'Position'); finite(y, 'Position'); finite(z, 'Position');
        owner.listenerPosition[0] = x; owner.listenerPosition[1] = y; owner.listenerPosition[2] = z;
        markListenerSpatial(owner);
    },
    listenerVelocity(id, x, y, z) {
        const owner = requireContext(id);
        owner.listenerVelocity[0] = finite(x, 'Velocity');
        owner.listenerVelocity[1] = finite(y, 'Velocity');
        owner.listenerVelocity[2] = finite(z, 'Velocity');
        markListenerSpatial(owner);
    },
    listenerOrientation(id, fx, fy, fz, ux, uy, uz) {
        const owner = requireContext(id);
        setListenerBasis(owner, fx, fy, fz, ux, uy, uz);
        markListenerSpatial(owner);
    },
    listenerGain(id, gain) {
        const owner = requireContext(id);
        owner.listenerGain = nonnegative(gain, 'Gain');
        activation.applyGain(owner);
    },
    createSource(id) { return createSource(requireContext(id)); },
    destroySource(id, sourceId) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        source.stream.dispose();
        detachNode(source); source.panner.disconnect(); source.gain.disconnect(); owner.sources.delete(sourceId);
        owner.sourceList.splice(owner.sourceList.indexOf(source), 1);
    },
    createBuffer(id) {
        const owner = requireContext(id);
        if (owner.buffers.size >= maxBuffers) throw new Error('WebAudio.BufferCapacityExceeded.');
        if (nextBuffer >= 0x7fffffff) throw new Error('WebAudio.BufferHandleExhausted.');
        const bufferId = ++nextBuffer;
        owner.buffers.set(bufferId, { audio: null, bytes: 0, bytesPerFrame: 0 });
        return bufferId;
    },
    destroyBuffer(id, bufferId) {
        const owner = requireContext(id), entry = requireBuffer(owner, bufferId);
        if (bufferInUse(owner, bufferId))
            throw new Error('WebAudio.BufferInUse: detach sources before destroying this buffer.');
        owner.pcmBytes -= entry.bytes;
        owner.buffers.delete(bufferId);
    },
    uploadBuffer(id, bufferId, pcm, frequency, channels, format) {
        const owner = requireContext(id), entry = requireBuffer(owner, bufferId);
        if (bufferInUse(owner, bufferId))
            throw new Error('WebAudio.BufferInUse: detach or unqueue a buffer before replacing its PCM.');
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
        entry.bytesPerFrame = channels * bytesPerSample;
    },
    play(id, sourceId) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        flushSourceSpatial(owner, source);
        if (!source.stream.count && !source.buffer) throw new Error('WebAudio.BufferMissing: attach PCM before playback.');
        if (!owner.activated) { source.deferredPlayback = true; source.state = 'playing'; return; }
        source.deferredPlayback = false;
        if (source.stream.count) { source.stream.play(source.rate); source.state = 'playing'; return; }
        if (source.playing) return;
        if (source.offset >= source.buffer.duration) source.offset = 0;
        const node = owner.context.createBufferSource();
        node.buffer = source.buffer;
        node.loop = source.loop;
        node.playbackRate.value = source.rate;
        node.connect(source.channels === 1 ? source.panner : source.gain);
        source.node = node;
        source.started = owner.context.currentTime;
        source.playing = true;
        source.state = 'playing';
        node.onended = () => {
            if (source.node !== node) return;
            source.node = null;
            source.playing = false;
            source.state = 'stopped';
            source.offset = 0;
            node.disconnect();
        };
        node.start(0, source.offset);
    },
    stop(id, sourceId) {
        const source = requireSource(requireContext(id), sourceId);
        source.stream.stop();
        source.deferredPlayback = false; source.state = 'stopped';
        detachNode(source); source.playing = false; source.offset = 0;
    },
    pause(id, sourceId) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        source.stream.pause();
        source.deferredPlayback = false; source.state = 'paused';
        source.offset = playbackOffset(owner, source);
        detachNode(source); source.playing = false;
    },
    rewind(id, sourceId) {
        const source = requireSource(requireContext(id), sourceId);
        source.stream.rewind();
        source.deferredPlayback = false; source.state = 'initial';
        detachNode(source); source.playing = false; source.offset = 0;
    },
    setSourceBuffer(id, sourceId, bufferId) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        const buffer = bufferId ? requireBuffer(owner, bufferId).audio : null;
        if (bufferId && !buffer) throw new Error('WebAudio.BufferEmpty: upload PCM before attaching.');
        source.stream.clear();
        detachNode(source); source.playing = false; source.offset = 0;
        source.bufferId = bufferId; source.buffer = buffer;
        source.deferredPlayback = false; source.state = 'initial';
        source.channels = buffer?.numberOfChannels ?? 1;
        source.bytesPerFrame = bufferId ? requireBuffer(owner, bufferId).bytesPerFrame : 0;
        markSourceSpatial(owner, source);
    },
    queueBuffers(id, sourceId, buffers) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        if (source.bufferId) throw new Error('WebAudio.StaticSource: detach the static buffer before queuing.');
        if (source.loop) throw new Error('WebAudio.StreamLoopUnsupported: stream producers must explicitly repeat queued content.');
        // The borrowed managed view cannot survive this call. Copy only at a queue boundary.
        const ids = buffers.slice();
        if (!ids.length) return;
        if (ids.length > maxBuffers - source.stream.count) throw new Error('WebAudio.StreamQueueCapacityExceeded.');
        const first = requireBuffer(owner, ids[0]);
        if (!first.audio) throw new Error('WebAudio.BufferEmpty: upload PCM before queuing.');
        const queuedAudio = source.stream.count ? source.stream.entry(0).audio : first.audio;
        const bytesPerFrame = source.stream.count ? source.bytesPerFrame : first.bytesPerFrame;
        for (let index = 0; index < ids.length; index++) {
            const entry = requireBuffer(owner, ids[index]);
            if (!entry.audio || entry.audio.sampleRate !== queuedAudio.sampleRate
                || entry.audio.numberOfChannels !== queuedAudio.numberOfChannels)
                throw new Error('WebAudio.StreamFormatMismatch: queued buffers must share their sample rate and channel count.');
            if (entry.bytesPerFrame !== bytesPerFrame)
                throw new Error('WebAudio.StreamFormatMismatch: queued buffers must share their PCM sample width.');
        }
        source.channels = first.audio.numberOfChannels;
        source.bytesPerFrame = bytesPerFrame;
        source.stream.destination = source.channels === 1 ? source.panner : source.gain;
        source.spatialDirty = true;
        flushSourceSpatial(owner, source);
        source.stream.enqueue(ids, bufferId => requireBuffer(owner, bufferId).audio);
    },
    unqueueProcessedBuffers(id, sourceId, output, maximum) {
        const source = requireSource(requireContext(id), sourceId);
        if (!Number.isInteger(maximum) || maximum < 0 || maximum > 32)
            throw new Error('WebAudio.StreamUnqueueLimit: at most 32 buffers per call.');
        const count = source.stream.unqueue(source.transfer, maximum);
        output.set(source.transfer);
        return count;
    },
    buffersProcessed(id, sourceId) {
        const source = requireSource(requireContext(id), sourceId);
        source.stream.refresh();
        return source.bufferId ? source.state === 'stopped' ? 1 : 0 : source.stream.processed;
    },
    buffersQueued(id, sourceId) {
        const source = requireSource(requireContext(id), sourceId);
        return source.bufferId ? 1 : source.stream.count;
    },
    sourcePosition(id, sourceId, x, y, z) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        finite(x, 'Position'); finite(y, 'Position'); finite(z, 'Position');
        source.position[0] = x; source.position[1] = y; source.position[2] = z;
        markSourceSpatial(owner, source);
    },
    sourceVelocity(id, sourceId, x, y, z) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        source.velocity[0] = finite(x, 'Velocity');
        source.velocity[1] = finite(y, 'Velocity');
        source.velocity[2] = finite(z, 'Velocity');
        markSourceSpatial(owner, source);
    },
    sourceGain(id, sourceId, gain) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        source.gainValue = nonnegative(gain, 'Gain');
        markSourceSpatial(owner, source);
    },
    sourcePitch(id, sourceId, pitch) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        if (!Number.isFinite(pitch) || pitch <= 0)
            throw new Error('WebAudio.PitchOutOfRange: playback rate must be positive and finite.');
        source.pitch = pitch;
        markSourceSpatial(owner, source);
    },
    sourceLooping(id, sourceId, loop) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        if (loop && source.stream.count)
            throw new Error('WebAudio.StreamLoopUnsupported: stream producers must explicitly repeat queued content.');
        source.offset = playbackOffset(owner, source);
        source.started = owner.context.currentTime;
        source.loop = Boolean(loop);
        if (source.node) source.node.loop = source.loop;
    },
    isSourcePlaying(id, sourceId) {
        const source = requireSource(requireContext(id), sourceId);
        source.stream.refresh();
        return source.deferredPlayback || (source.stream.count ? source.stream.playing : source.playing);
    },
    sampleOffset(id, sourceId) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        if (source.stream.count) return source.stream.sampleOffset();
        return source.buffer ? Math.floor(playbackOffset(owner, source) * source.buffer.sampleRate) : 0;
    },
    sourceRelative(id, sourceId, relative) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        source.relative = Boolean(relative);
        markSourceSpatial(owner, source);
    },
    sourceDirection(id, sourceId, x, y, z) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        finite(x, 'Direction'); finite(y, 'Direction'); finite(z, 'Direction');
        source.direction[0] = x; source.direction[1] = y; source.direction[2] = z;
        markSourceSpatial(owner, source);
    },
    sourceFloatProperty(id, sourceId, property) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        switch (property) {
            case 0: return source.referenceDistance;
            case 1: return source.maxDistance;
            case 2: return source.rolloff;
            case 3: return source.pitch;
            case 4: return source.minGain;
            case 5: return source.maxGain;
            case 6: return source.gainValue;
            case 7: return source.coneInner;
            case 8: return source.coneOuter;
            case 9: return source.coneOuterGain;
            case 10: return offsetValue(owner, source, 0);
            default: throw new Error('WebAudio.SourcePropertyUnsupported.');
        }
    },
    setSourceFloatProperty(id, sourceId, property, value) {
        const owner = requireContext(id), source = requireSource(owner, sourceId);
        switch (property) {
            case 0: source.referenceDistance = nonnegative(value, 'ReferenceDistance'); break;
            case 1: source.maxDistance = nonnegative(value, 'MaxDistance', true); break;
            case 2: source.rolloff = nonnegative(value, 'RolloffFactor'); break;
            case 3: engineAudioImports.sourcePitch(id, sourceId, value); return;
            case 4: source.minGain = bounded(value, 1, 'MinGain'); break;
            case 5: source.maxGain = bounded(value, 1, 'MaxGain'); break;
            case 6: engineAudioImports.sourceGain(id, sourceId, value); return;
            case 7: source.coneInner = bounded(value, 360, 'ConeInnerAngle'); break;
            case 8: source.coneOuter = bounded(value, 360, 'ConeOuterAngle'); break;
            case 9: source.coneOuterGain = bounded(value, 1, 'ConeOuterGain'); break;
            case 10: engineAudioImports.seekSource(id, sourceId, 0, value); return;
            default: throw new Error('WebAudio.SourcePropertyUnsupported.');
        }
        markSourceSpatial(owner, source);
    },
    sourceQueueOffset(id, sourceId, unit) {
        const owner = requireContext(id);
        return offsetValue(owner, requireSource(owner, sourceId), unit);
    },
    seekSource(id, sourceId, unit, value) {
        const owner = requireContext(id), source = requireSource(owner, sourceId), audio = queueAudio(source);
        nonnegative(value, 'Offset');
        if (!audio) throw new Error('WebAudio.BufferMissing: attach or queue PCM before seeking.');
        let seconds;
        if (unit === 0) seconds = Math.floor(value * audio.sampleRate) / audio.sampleRate;
        else if (unit === 1) seconds = Math.floor(value) / audio.sampleRate;
        else if (unit === 2) seconds = Math.floor(value / source.bytesPerFrame) / audio.sampleRate;
        else throw new Error('WebAudio.OffsetUnitUnsupported.');
        if (source.stream.count) { source.stream.seek(seconds); return; }
        if (seconds >= audio.duration) throw new Error('WebAudio.OffsetOutOfRange: the seek must remain within the attached PCM.');
        const wasPlaying = source.playing;
        detachNode(source);
        source.playing = false;
        source.offset = seconds;
        if (wasPlaying) engineAudioImports.play(id, sourceId);
    },

};
