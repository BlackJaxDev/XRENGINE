import assert from 'node:assert/strict';
import { engineAudioImports as audio } from '../../XREngine.Audio.WebAudio/Assets/engine-audio.js';

// This fixture records Web Audio buffers. It does not test an output device.
class RecordingAudioContext {
    static current;
    constructor() { RecordingAudioContext.current = this; this.state = 'suspended'; this.sampleRate = 48000; this.buffers = []; }
    createGain() { return { gain: { value: 0 }, connect() {}, disconnect() {} }; }
    suspend() { return Promise.resolve(); }
    close() { this.state = 'closed'; return Promise.resolve(); }
    createBuffer(channels, frames, rate) {
        const data = Array.from({ length: channels }, () => new Float32Array(frames));
        const buffer = { numberOfChannels: channels, length: frames, sampleRate: rate, duration: frames / rate,
            getChannelData: channel => data[channel] };
        this.buffers.push(buffer);
        return buffer;
    }
}
globalThis.AudioContext = RecordingAudioContext;
const owner = audio.open();
let checks = 0;
try {
    const cases = [
        { format: 0, bytes: [0, 128, 255, 64], values: [-1, 0, 127 / 128, -0.5] },
        { format: 1, bytes: [0, 128, 0, 0, 255, 127, 0, 64], values: [-1, 0, 32767 / 32768, 0.5] },
        { format: 2, bytes: [0, 0, 128, 191, 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 128, 62], values: [-1, 0, 1, 0.25] },
    ];
    for (const fixture of cases) for (const channels of [1, 2]) {
        const handle = audio.createBuffer(owner), input = new Uint8Array(fixture.bytes);
        audio.uploadBuffer(owner, handle, input, 48000, channels, fixture.format);
        input.fill(0); // An uploaded buffer must not retain a borrowed managed byte view.
        const actual = RecordingAudioContext.current.buffers.at(-1);
        for (let channel = 0; channel < channels; channel++)
            assert.deepEqual([...actual.getChannelData(channel)], fixture.values.filter((_, i) => i % channels === channel));
        assert.equal(actual.sampleRate, 48000);
        audio.destroyBuffer(owner, handle);
        checks++;
    }
    const handle = audio.createBuffer(owner);
    for (const [frequency, channels, format, bytes] of [
        [7999, 1, 0, [0]], [192001, 1, 0, [0]], [48000, 0, 0, [0]],
        [48000, 3, 0, [0, 0, 0]], [48000, 1, 3, [0]],
        [48000, 1, 0, []], [48000, 2, 1, [0, 0]],
        [48000, 1, 2, [0, 0, 192, 127]], [48000, 1, 2, [0, 0, 128, 127]],
    ]) {
        assert.throws(() => audio.uploadBuffer(owner, handle, new Uint8Array(bytes), frequency, channels, format), /WebAudio.Pcm/);
        checks++;
    }
    audio.uploadBuffer(owner, handle, new Uint8Array(8 * 1024 * 1024), 8000, 1, 0);
    const overflow = audio.createBuffer(owner);
    assert.throws(() => audio.uploadBuffer(owner, overflow, new Uint8Array([0]), 48000, 1, 0), /WebAudio.PcmBudgetExceeded/);
    audio.destroyBuffer(owner, handle);
    audio.uploadBuffer(owner, overflow, new Uint8Array([0]), 192000, 1, 0);
    audio.destroyBuffer(owner, overflow);
    checks++;
    console.log(`PASS ${checks} PCM conversion, rejection, ownership and context-budget checks`);
} finally { audio.close(owner); }
