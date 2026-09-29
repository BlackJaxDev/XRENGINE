const MAX_BUFFERS = 32;
const MAX_VOICES = 32;
const MAX_ENCODED_BYTES = 8 * 1024 * 1024;
const MAX_PENDING_BYTES = 16 * 1024 * 1024;
const MAX_DECODED_BYTES = 32 * 1024 * 1024;
const MAX_PENDING_DECODES = 2;
const DECODE_TIMEOUT_MS = 30000;
const RESUME_TIMEOUT_MS = 5000;
const DEMO_CLIP = 'engine-demo-step';

/** Canvas-owned Web Audio adapter. Native decoders and device audio are never used. */
export class BrowserAudioService {
    constructor({ onState } = {}) {
        this.onState = onState;
        this.state = 'pending';
        this.reason = 'Audio requires a user gesture.';
        this.context = null;
        this.master = null;
        this.buffers = new Map();
        this.voices = new Map();
        this.pending = new Map();
        this.pendingBytes = 0;
        this.decodedBytes = 0;
        this.nextVoice = 1;
        this.epoch = 0;
        this.visible = true;
        this.paused = false;
        this.closed = false;
        this.masterGain = 0.5;
        this.resumePromise = null;
        this.resumeTimer = 0;
        this.cancelResume = null;
        this._contextStateChanged = () => this._syncState();
    }

    get supported() {
        return typeof (globalThis.AudioContext ?? globalThis.webkitAudioContext) === 'function';
    }

    /** Call directly from a gesture handler, before awaiting other work. */
    unlock() {
        if (this.closed) return Promise.resolve(false);
        if (!this.visible) {
            this._setState('suspended', 'Audio is suspended while the page is hidden.');
            return Promise.resolve(false);
        }
        this.paused = false;
        try {
            if (!this.context) {
                const AudioContextType = globalThis.AudioContext ?? globalThis.webkitAudioContext;
                if (!AudioContextType) throw new Error('Web Audio is unavailable in this browser.');
                const context = new AudioContextType({ latencyHint: 'interactive' });
                this.context = context;
                try {
                    this.master = context.createGain();
                    this.master.gain.value = this.masterGain;
                    this.master.connect(context.destination);
                    context.addEventListener('statechange', this._contextStateChanged);
                    this._createDemoClip();
                } catch (error) {
                    context.removeEventListener('statechange', this._contextStateChanged);
                    this.master?.disconnect();
                    this.master = null;
                    this.context = null;
                    this.buffers.clear();
                    this.decodedBytes = 0;
                    void context.close().catch(() => {});
                    throw error;
                }
            }
            if (this.context.state === 'closed') throw new Error('The audio context has closed.');
            if (this.context.state === 'running') {
                this._syncState();
                return Promise.resolve(this.state === 'ready');
            }
            // Always issue resume inside this call; a previous pending attempt must not
            // consume the next real gesture without asking the browser to resume again.
            const operation = this.context.resume();
            if (this.resumePromise) {
                void operation.catch(() => {});
                return this.resumePromise;
            }
            const epoch = this.epoch;
            this._setState('pending', 'Waiting for browser audio activation.');
            this.resumePromise = new Promise(resolve => {
                let settled = false;
                const finish = success => {
                    if (settled) return;
                    settled = true;
                    clearTimeout(this.resumeTimer);
                    this.resumeTimer = 0;
                    this.resumePromise = null;
                    this.cancelResume = null;
                    resolve(success);
                };
                this.cancelResume = () => finish(false);
                this.resumeTimer = setTimeout(() => {
                    if (epoch === this.epoch && !this.closed)
                        this._setState('denied', 'Audio activation did not finish; tap Enable audio again.');
                    finish(false);
                }, RESUME_TIMEOUT_MS);
                void operation.then(() => {
                    if (epoch !== this.epoch || this.closed) return finish(false);
                    this._syncState();
                    finish(this.state === 'ready');
                }, error => {
                    if (epoch === this.epoch && !this.closed)
                        this._setState('denied', `Audio activation failed: ${error.message}`);
                    finish(false);
                });
            });
            return this.resumePromise;
        } catch (error) {
            this._setState('denied', error.message);
            return Promise.resolve(false);
        }
    }

    resume() { return this.unlock(); }

    pause() {
        if (this.closed) return Promise.resolve();
        this.paused = true;
        this.cancelResume?.();
        this._setState('suspended', 'Audio is paused; a user gesture is needed to resume.');
        return this._suspendContext();
    }

    setVisible(visible) {
        visible = Boolean(visible);
        if (visible === this.visible) return;
        this.visible = visible;
        if (this.closed) return;
        if (!this.visible) {
            this.cancelResume?.();
            this._setState('suspended', 'Audio is suspended while the page is hidden.');
            void this._suspendContext();
        } else {
            // Returning to the page never bypasses its user-gesture activation policy.
            this._syncState();
        }
    }

    /** Decode complete browser-supported audio bytes; unsupported codecs reject explicitly. */
    async load(id, bytes, signal) {
        this._requireOpen();
        if (!this.context) throw new Error('Enable audio before decoding audio content.');
        if (typeof id !== 'string' || !/^[a-zA-Z0-9_.:-]{1,128}$/.test(id))
            throw new Error('Audio asset ID must contain 1–128 safe identifier characters.');
        if (this.buffers.has(id) || this.pending.has(id))
            throw new Error(`Audio asset ${id} is already loaded or decoding.`);
        if (!(bytes instanceof Uint8Array) || bytes.byteLength < 1 || bytes.byteLength > MAX_ENCODED_BYTES)
            throw new Error('Encoded audio must contain 1 byte to 8 MiB.');
        if (this.buffers.size + this.pending.size >= MAX_BUFFERS || this.pending.size >= MAX_PENDING_DECODES ||
            this.pendingBytes + bytes.byteLength > MAX_PENDING_BYTES)
            throw new Error('The bounded audio buffer or pending-decode budget is full.');
        if (signal?.aborted) throw new DOMException('Audio load was cancelled.', 'AbortError');
        const epoch = this.epoch;
        const context = this.context;
        const pending = { bytes: bytes.byteLength, cancelled: false, cancel: null };
        this.pending.set(id, pending);
        this.pendingBytes += pending.bytes;
        let timer = 0;
        let abortHandler;
        const cancelled = new Promise((_, reject) => {
            pending.cancel = reason => {
                pending.cancelled = true;
                reject(reason);
            };
            abortHandler = () => pending.cancel(new DOMException('Audio load was cancelled.', 'AbortError'));
            signal?.addEventListener('abort', abortHandler, { once: true });
            timer = setTimeout(() => pending.cancel(new Error('Audio decode exceeded its 30-second deadline.')),
                DECODE_TIMEOUT_MS);
        });
        // decodeAudioData may detach its input. Copy only this bounded view, not its backing allocation.
        // Browsers cannot cancel their native decoder or cap its temporary allocation. Reservations
        // remain held until it settles, even when cancellation has already returned to the caller.
        const decode = Promise.resolve().then(() => {
            if (pending.cancelled || this.closed || epoch !== this.epoch)
                throw new DOMException('Audio owner was replaced.', 'AbortError');
            return context.decodeAudioData(bytes.slice().buffer);
        }).then(buffer => {
            if (pending.cancelled || this.closed || epoch !== this.epoch)
                throw new DOMException('Audio owner was replaced.', 'AbortError');
            const size = buffer.length * buffer.numberOfChannels * Float32Array.BYTES_PER_ELEMENT;
            if (buffer.numberOfChannels < 1 || buffer.numberOfChannels > 2 || buffer.duration > 120 ||
                buffer.sampleRate > 96000 || !Number.isSafeInteger(size) || size < 1 ||
                this.decodedBytes + size > MAX_DECODED_BYTES)
                throw new Error('Decoded audio exceeds the stereo, 120-second, 96 kHz or 32 MiB budget.');
            this.buffers.set(id, { buffer, bytes: size });
            this.decodedBytes += size;
            return { id, duration: buffer.duration, channels: buffer.numberOfChannels,
                sampleRate: buffer.sampleRate, decodedBytes: size };
        }, error => {
            if (error.name === 'AbortError') throw error;
            throw new Error(`Browser audio decode failed (the codec may be unsupported): ${error.message}`);
        }).finally(() => {
            if (this.pending.get(id) === pending) {
                this.pending.delete(id);
                this.pendingBytes -= pending.bytes;
            }
        });
        try { return await Promise.race([decode, cancelled]); }
        finally {
            clearTimeout(timer);
            signal?.removeEventListener('abort', abortHandler);
        }
    }

    play(id, { gain = 1, loop = false, spatial = true, position, forward } = {}) {
        this._requireOpen();
        this._syncState();
        if (this.state !== 'ready') throw new Error('Audio playback requires a running, user-activated context.');
        const asset = this.buffers.get(id);
        if (!asset) throw new Error(`Audio asset ${id} is not loaded.`);
        if (this.voices.size >= MAX_VOICES) throw new Error('The 32-voice audio budget is full.');
        this._checkGain(gain);
        const source = this.context.createBufferSource();
        const volume = this.context.createGain();
        let panner = null;
        const voiceId = this.nextVoice++;
        try {
            source.buffer = asset.buffer;
            source.loop = Boolean(loop);
            volume.gain.value = gain;
            source.connect(volume);
            if (spatial) {
                panner = this.context.createPanner();
                panner.panningModel = 'equalpower';
                panner.distanceModel = 'inverse';
                panner.refDistance = 1;
                panner.maxDistance = 100;
                panner.rolloffFactor = 1;
                volume.connect(panner);
                panner.connect(this.master);
            } else volume.connect(this.master);
            this.voices.set(voiceId, { id, source, volume, panner });
            if (position) this.setSourceTransform(voiceId, position, forward);
            source.onended = () => this._releaseVoice(voiceId);
            source.start();
            return voiceId;
        } catch (error) {
            this.voices.delete(voiceId);
            source.onended = null;
            source.disconnect();
            volume.disconnect();
            panner?.disconnect();
            source.buffer = null;
            throw error;
        }
    }

    setSourceTransform(voiceId, position, forward) {
        this.setSourceValues(voiceId, position[0], position[1], position[2],
            forward?.[0] ?? 0, forward?.[1] ?? 0, forward?.[2] ?? -1);
    }

    setSourceValues(voiceId, x, y, z, fx = 0, fy = 0, fz = -1) {
        const panner = this.voices.get(voiceId)?.panner;
        if (!panner || this.closed) return false;
        this._checkVector(x, y, z);
        this._checkDirection(fx, fy, fz);
        if (panner.positionX) {
            panner.positionX.value = x; panner.positionY.value = y; panner.positionZ.value = z;
            panner.orientationX.value = fx; panner.orientationY.value = fy; panner.orientationZ.value = fz;
        } else {
            panner.setPosition(x, y, z);
            panner.setOrientation(fx, fy, fz);
        }
        return true;
    }

    setListenerTransform(position, forward, up) {
        this.setListenerValues(position[0], position[1], position[2],
            forward[0], forward[1], forward[2], up[0], up[1], up[2]);
    }

    setListenerValues(x, y, z, fx, fy, fz, ux, uy, uz) {
        const listener = this.context?.listener;
        if (!listener || this.closed) return false;
        this._checkVector(x, y, z);
        this._checkDirection(fx, fy, fz);
        this._checkDirection(ux, uy, uz);
        const cx = fy * uz - fz * uy;
        const cy = fz * ux - fx * uz;
        const cz = fx * uy - fy * ux;
        if (cx * cx + cy * cy + cz * cz < 1e-12)
            throw new Error('Audio listener forward and up directions must not be parallel.');
        // Engine and Web Audio both use right-handed +Y up, -Z forward coordinates.
        if (listener.positionX) {
            listener.positionX.value = x; listener.positionY.value = y; listener.positionZ.value = z;
            listener.forwardX.value = fx; listener.forwardY.value = fy; listener.forwardZ.value = fz;
            listener.upX.value = ux; listener.upY.value = uy; listener.upZ.value = uz;
        } else {
            listener.setPosition(x, y, z);
            listener.setOrientation(fx, fy, fz, ux, uy, uz);
        }
        return true;
    }

    setGain(gain, voiceId) {
        this._checkGain(gain);
        this._requireOpen();
        if (voiceId === undefined) {
            this.masterGain = gain;
            if (this.master) this.master.gain.value = gain;
            return true;
        }
        const voice = this.voices.get(voiceId);
        if (!voice) return false;
        voice.volume.gain.value = gain;
        return true;
    }

    stop(voiceId) {
        const voice = this.voices.get(voiceId);
        if (!voice) return false;
        voice.source.stop();
        this._releaseVoice(voiceId);
        return true;
    }

    unload(id) {
        const pending = this.pending.get(id);
        pending?.cancel(new DOMException('Audio asset was unloaded.', 'AbortError'));
        const asset = this.buffers.get(id);
        if (!asset) return Boolean(pending);
        for (const [voiceId, voice] of this.voices)
            if (voice.id === id) this.stop(voiceId);
        this.buffers.delete(id);
        this.decodedBytes -= asset.bytes;
        return true;
    }

    dispose() {
        if (this.closed) return;
        this.closed = true;
        this.epoch++;
        this.cancelResume?.();
        for (const pending of this.pending.values())
            pending.cancel(new DOMException('Audio service was disposed.', 'AbortError'));
        for (const voiceId of this.voices.keys()) this.stop(voiceId);
        this.buffers.clear();
        this.decodedBytes = 0;
        const context = this.context;
        context?.removeEventListener('statechange', this._contextStateChanged);
        this.master?.disconnect();
        this.master = null;
        this.context = null;
        if (context && context.state !== 'closed') void context.close().catch(() => {});
        this._setState('closed', 'Audio service disposed.');
        this.onState = null;
    }

    getStatistics() {
        return { state: this.state, reason: this.reason, supported: this.supported,
            contextState: this.context?.state ?? 'none',
            buffers: this.buffers.size, voices: this.voices.size, decodedBytes: this.decodedBytes,
            pendingDecodes: this.pending.size, pendingEncodedBytes: this.pendingBytes,
            maxBuffers: MAX_BUFFERS, maxVoices: MAX_VOICES, maxDecodedBytes: MAX_DECODED_BYTES,
            maxEncodedBytes: MAX_ENCODED_BYTES, maxPendingBytes: MAX_PENDING_BYTES,
            builtinClip: DEMO_CLIP };
    }

    _createDemoClip() {
        const sampleRate = this.context.sampleRate;
        const length = Math.ceil(sampleRate * 0.14);
        const buffer = this.context.createBuffer(1, length, sampleRate);
        const samples = buffer.getChannelData(0);
        // A deterministic, quiet short step pulse; generating it requires no codec or download.
        for (let i = 0; i < length; i++) {
            const time = i / sampleRate;
            const envelope = Math.min(time / 0.006, 1) * Math.exp(-time * 38) * (1 - i / length);
            samples[i] = 0.3 * envelope * (Math.sin(2 * Math.PI * 170 * time) +
                0.2 * Math.sin(2 * Math.PI * 370 * time));
        }
        const bytes = length * Float32Array.BYTES_PER_ELEMENT;
        this.buffers.set(DEMO_CLIP, { buffer, bytes });
        this.decodedBytes += bytes;
    }

    _releaseVoice(voiceId) {
        const voice = this.voices.get(voiceId);
        if (!voice) return;
        this.voices.delete(voiceId);
        voice.source.onended = null;
        voice.source.disconnect();
        voice.volume.disconnect();
        voice.panner?.disconnect();
        voice.source.buffer = null;
    }

    _suspendContext() {
        const context = this.context;
        if (!context || context.state === 'closed') return Promise.resolve();
        return context.suspend().catch(error => {
            if (!this.closed) this._setState('denied', `Audio suspension failed: ${error.message}`);
        });
    }

    _syncState() {
        if (this.closed) return;
        if (!this.context) return this._setState('pending', 'Audio requires a user gesture.');
        if (this.context.state === 'closed') return this._setState('closed', 'The browser closed audio.');
        if (this.context.state === 'running' && this.visible && !this.paused)
            return this._setState('ready', 'Browser audio is running.');
        this._setState('suspended', 'Audio is suspended; tap Enable audio to resume.');
    }

    _setState(state, reason) {
        if (state === this.state && reason === this.reason) return;
        this.state = state;
        this.reason = reason;
        try { this.onState?.(state, reason); }
        catch (error) { console.error('Audio state observer failed.', error); }
    }

    _requireOpen() {
        if (this.closed || this.context?.state === 'closed') throw new Error('The audio service is closed.');
    }

    _checkGain(gain) {
        if (!Number.isFinite(gain) || gain < 0 || gain > 1)
            throw new Error('Audio gain must be finite and between zero and one.');
    }

    _checkVector(x, y, z) {
        if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(z) ||
            Math.abs(x) > 1e7 || Math.abs(y) > 1e7 || Math.abs(z) > 1e7)
            throw new Error('Audio vectors must be finite and within the supported world extent.');
    }

    _checkDirection(x, y, z) {
        this._checkVector(x, y, z);
        if (x * x + y * y + z * z < 1e-12) throw new Error('Audio directions must be nonzero.');
    }
}
