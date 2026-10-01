const capacity = 32;

/** Bounded PCM queue scheduled on the audio clock, independent of frame and ended-event timing. */
export class WebAudioStream {
    constructor(context, destination) {
        this.context = context;
        this.destination = destination;
        this.entries = Array.from({ length: capacity }, () =>
            ({ id: 0, audio: null, node: null, start: 0, end: 0, offset: 0 }));
        this.head = 0;
        this.count = 0;
        this.processed = 0;
        this.playing = false;
        this.stopped = false;
        this.rate = 1;
        this.closed = false;
    }

    entry(index) { return this.entries[(this.head + index) % capacity]; }

    refresh() {
        if (!this.playing) return;
        const now = this.context.currentTime;
        while (this.processed < this.count && this.entry(this.processed).end <= now) {
            this.releaseNode(this.entry(this.processed++));
        }
        if (this.processed === this.count) { this.playing = false; this.stopped = true; }
    }

    contains(id) {
        for (let index = 0; index < this.count; index++)
            if (this.entry(index).id === id) return true;
        return false;
    }

    enqueue(ids, resolve) {
        if (this.closed) throw new Error('WebAudio.StreamClosed.');
        if (ids.length > capacity - this.count) throw new Error('WebAudio.StreamQueueCapacityExceeded: at most 32 buffers per source.');
        // Validate the complete batch before taking any ownership or scheduling nodes.
        const first = this.count ? this.entry(0).audio : ids.length ? resolve(ids[0]) : null;
        for (let index = 0; index < ids.length; index++) {
            const audio = resolve(ids[index]);
            if (!audio) throw new Error('WebAudio.BufferEmpty: upload PCM before queuing.');
            if (audio.sampleRate !== first.sampleRate || audio.numberOfChannels !== first.numberOfChannels)
                throw new Error('WebAudio.StreamFormatMismatch: queued buffers must share their sample rate and channel count.');
        }
        this.refresh();
        const previousCount = this.count;
        for (let index = 0; index < ids.length; index++) {
            const entry = this.entry(this.count++);
            entry.id = ids[index];
            entry.audio = resolve(ids[index]);
            entry.offset = 0;
        }
        // Match the transport's stopped-source contract: newly queued buffers
        // become processed until Play or Rewind starts a new playback sequence.
        if (this.stopped) this.processed = this.count;
        if (this.playing) {
            try { this.schedule(previousCount, this.entry(previousCount - 1).end); }
            catch (error) {
                for (let index = previousCount; index < this.count; index++) {
                    const entry = this.entry(index);
                    this.releaseNode(entry);
                    entry.id = 0;
                    entry.audio = null;
                }
                this.count = previousCount;
                throw error;
            }
        }
    }

    schedule(first, when) {
        for (let index = first; index < this.count; index++) {
            const entry = this.entry(index);
            const node = this.context.createBufferSource();
            entry.node = node;
            node.buffer = entry.audio;
            node.playbackRate.value = this.rate;
            node.connect(this.destination);
            entry.start = when;
            entry.end = when + (entry.audio.duration - entry.offset) / this.rate;
            node.start(when, entry.offset);
            when = entry.end;
        }
    }

    play(rate) {
        this.refresh();
        if (this.playing || !this.count) return;
        if (this.processed === this.count) this.rewind();
        this.rate = rate;
        try {
            this.schedule(this.processed, this.context.currentTime);
            this.playing = true;
            this.stopped = false;
        } catch (error) { this.stop(); throw error; }
    }

    offset() {
        this.refresh();
        if (this.processed === this.count) return 0;
        const entry = this.entry(this.processed);
        return this.playing
            ? Math.min(entry.audio.duration, entry.offset + Math.max(0, this.context.currentTime - entry.start) * this.rate)
            : entry.offset;
    }

    sampleOffset() {
        const offset = this.offset();
        return this.processed === this.count ? 0 : Math.floor(offset * this.entry(this.processed).audio.sampleRate);
    }

    pause() {
        this.refresh();
        if (!this.playing) return;
        const offset = this.offset();
        if (this.processed === this.count) return;
        this.entry(this.processed).offset = offset;
        this.playing = false;
        for (let index = this.processed; index < this.count; index++) this.releaseNode(this.entry(index));
    }

    setRate(rate) {
        if (rate === this.rate) return;
        const wasPlaying = this.playing;
        this.pause();
        this.rate = rate;
        // AudioBufferSourceNode start times are immutable. Only a changed rate
        // reschedules the bounded future queue; steady-state polling allocates nothing.
        if (wasPlaying && this.processed < this.count) this.play(rate);
    }

    releaseNode(entry) {
        if (!entry.node) return;
        try { entry.node.stop(); } catch { /* A naturally finished node is already stopped. */ }
        entry.node.disconnect();
        entry.node = null;
    }

    stop() {
        this.playing = false;
        this.stopped = true;
        for (let index = 0; index < this.count; index++) {
            const entry = this.entry(index);
            this.releaseNode(entry);
            entry.offset = 0;
        }
        this.processed = this.count;
    }

    rewind() { this.stop(); this.processed = 0; this.stopped = false; }

    unqueue(output, maximum = output.length) {
        this.refresh();
        const count = Math.min(output.length, maximum, this.processed);
        for (let index = 0; index < count; index++) {
            const entry = this.entry(index);
            output[index] = entry.id;
            this.releaseNode(entry);
            entry.id = 0;
            entry.audio = null;
            entry.offset = 0;
        }
        this.head = (this.head + count) % capacity;
        this.count -= count;
        this.processed -= count;
        return count;
    }

    clear() {
        this.stop();
        for (let index = 0; index < this.count; index++) {
            const entry = this.entry(index);
            entry.id = 0;
            entry.audio = null;
        }
        this.head = this.count = this.processed = 0;
        this.stopped = false;
    }

    dispose() { this.clear(); this.closed = true; }
}
