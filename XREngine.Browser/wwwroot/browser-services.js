const excluded = Object.freeze({
    'native-xr': 'Native OpenXR/OpenVR sessions are outside the browser canvas profile.',
    'native-physics': 'PhysX/Jolt native bindings are excluded from the portable browser graph.',
    'native-editor': 'Native ImGui/Ultralight editor services are excluded; use browser DOM controls.',
    'video-decoding': 'Native FFmpeg playback is not included in this profile.',
});

/** Required services gate simulation; optional exclusions remain visible in diagnostics. */
export class BrowserServicePolicy {
    constructor(audio, demo, onChange) {
        this.audio = audio;
        this.demo = demo;
        this.onChange = onChange;
        this.required = ['dom-ui'];
        this.optional = ['web-audio'];
        this.waiters = new Set();
        this.disposed = false;
    }

    configure(services) {
        if (services) {
            this.required = [...services.required];
            this.optional = [...services.optional];
        }
        for (const service of this.required) {
            const reason = this.exclusion(service);
            if (reason) throw new Error(`Required service ${service} is unavailable: ${reason}`);
        }
        this.changed();
    }

    exclusion(service) {
        if (service === 'dom-ui') return null;
        if (service === 'web-audio') return this.audio.supported ? null : 'Web Audio is unavailable in this browser.';
        if (service === 'cpu-animation' || service === 'character-collision')
            return this.demo ? null : 'The static cooked profile has no animation/collision payload. These services are admitted only by the built-in interactive sample.';
        return excluded[service] ?? 'No implementation is registered for this browser service.';
    }

    get ready() {
        if (this.disposed) return false;
        for (const service of this.required)
            if (this.exclusion(service) || (service === 'web-audio' && this.audio.state !== 'ready')) return false;
        return true;
    }

    changed() {
        for (const waiter of this.waiters) waiter();
        this.onChange?.();
    }

    waitReady(signal) {
        signal.throwIfAborted();
        if (this.ready) return Promise.resolve();
        return new Promise((resolve, reject) => {
            const clear = () => { this.waiters.delete(check); signal.removeEventListener('abort', abort); };
            const abort = () => { clear(); reject(signal.reason); };
            const check = () => {
                if (this.disposed) { clear(); reject(new Error('Browser services were disposed.')); }
                else if (this.ready) { clear(); resolve(); }
            };
            this.waiters.add(check);
            signal.addEventListener('abort', abort, { once: true });
            check();
        });
    }

    getStatistics() {
        const describe = name => ({ name, ready: !this.exclusion(name)
            && (name !== 'web-audio' || this.audio.state === 'ready'),
            reason: this.exclusion(name) ?? (name === 'web-audio' ? this.audio.reason : null) });
        return { ready: this.ready, required: this.required.map(describe), optional: this.optional.map(describe), excluded };
    }

    dispose() { this.disposed = true; this.changed(); this.waiters.clear(); this.onChange = null; }
}
