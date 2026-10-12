/** Composes page/surface blockers and rejects completions from retired output generations. */
export class WebAudioActivation {
    constructor(contexts, owners, activateSources) {
        this.contexts = contexts;
        this.owners = owners;
        this.activateSources = activateSources;
        this.generation = 0;
        this.pageActive = true;
        this.surfaceActive = true;
        this.gestureActivated = false;
    }

    get active() { return this.pageActive && this.surfaceActive; }

    get ready() {
        if (!this.active || !this.gestureActivated || !this.owners.length) return false;
        for (let index = 0; index < this.owners.length; index++) {
            const owner = this.owners[index];
            if (!owner.activated || owner.activationFailure || owner.context.state !== 'running') return false;
        }
        return true;
    }

    get failure() {
        for (let index = 0; index < this.owners.length; index++)
            if (this.owners[index].activationFailure) return this.owners[index].activationFailure;
        return '';
    }

    get state() { return !this.owners.length ? 'pending' : this.failure ? 'failed' : this.ready ? 'ready' : 'suspended'; }

    notify() { globalThis.dispatchEvent?.(new Event('xrengine-audio-statechange')); }
    owns(owner) { return this.contexts.get(owner.id) === owner; }
    applyGain(owner) {
        owner.master.gain.value = this.active && owner.activated && !owner.activationFailure && owner.context.state === 'running'
            ? owner.listenerGain : 0;
    }

    opened(owner) {
        this.generation++;
        owner.context.onstatechange = () => {
            if (!this.owns(owner)) return;
            if (owner.context.state === 'closed')
                owner.activationFailure = 'WebAudio.ContextClosed: the browser retired an owned output.';
            this.applyGain(owner);
            if (!this.active && owner.context.state === 'running') this.suspendOwner(owner, this.generation);
            this.notify();
        };
        if (this.active && this.gestureActivated) void this.resumeOwners().catch(() => {});
        else this.suspendOwner(owner, this.generation);
        this.notify();
    }

    closed(owner) {
        owner.context.onstatechange = null;
        this.generation++;
        if (this.active && this.gestureActivated && this.owners.length)
            void this.resumeOwners().catch(() => {});
        this.notify();
    }

    setPageActive(active) { this.setBlocker('pageActive', Boolean(active)); }
    setSurfaceActive(active) { this.setBlocker('surfaceActive', Boolean(active)); }
    setBlocker(property, active) {
        if (this[property] === active) return;
        this[property] = active;
        this.generation++;
        if (this.active && this.gestureActivated) void this.resumeOwners().catch(() => {});
        else {
            for (let index = 0; index < this.owners.length; index++)
                this.suspendOwner(this.owners[index], this.generation);
        }
        this.notify();
    }

    suspendOwner(owner, generation) {
        owner.master.gain.value = 0;
        try {
            void owner.context.suspend().then(() => {
                if (generation === this.generation && this.owns(owner)) this.notify();
            }, error => this.recordFailure(owner, generation, 'Suspend', error));
        } catch (error) { this.recordFailure(owner, generation, 'Suspend', error); }
    }

    recordFailure(owner, generation, action, error) {
        if (generation !== this.generation || !this.owns(owner)) return;
        owner.activationFailure = `WebAudio.${action}Failed: ${error.message ?? error}`;
        owner.master.gain.value = 0;
        this.notify();
    }

    unlock() {
        if (!this.active || !this.owners.length) return Promise.resolve(false);
        if (globalThis.navigator?.userActivation?.isActive === false)
            return Promise.reject(new Error('WebAudio.GestureRequired: enable output directly from a trusted page gesture.'));
        this.gestureActivated = true;
        this.generation++;
        return this.resumeOwners();
    }

    resumeOwners() {
        const generation = this.generation;
        const pending = [];
        // Every resume is issued synchronously, before yielding the trusted gesture.
        for (let index = 0; index < this.owners.length; index++) {
            const owner = this.owners[index];
            owner.activationFailure = '';
            let resume;
            try { resume = owner.context.resume(); }
            catch (error) { this.recordFailure(owner, generation, 'Resume', error); return Promise.reject(error); }
            pending.push(resume.then(() => {
                if (generation !== this.generation || !this.owns(owner) || !this.active) return false;
                if (owner.context.state !== 'running') return false;
                try {
                    owner.activated = true;
                    this.activateSources(owner);
                    this.applyGain(owner);
                } catch (error) {
                    this.recordFailure(owner, generation, 'PlaybackActivation', error);
                    throw error;
                }
                this.notify();
                return true;
            }, error => {
                if (generation !== this.generation || !this.owns(owner)) return false;
                this.recordFailure(owner, generation, 'Resume', error);
                throw error;
            }));
        }
        return Promise.all(pending).then(() => generation === this.generation && this.ready);
    }
}
