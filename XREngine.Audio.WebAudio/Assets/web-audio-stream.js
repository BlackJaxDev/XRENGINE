const capacity = 32;
const maxLoopBytes = 8 * 1024 * 1024;
const maxRetiredNodes = 64;

function effectiveRate(rate) {
    const effective = Math.fround(rate);
    if (!Number.isFinite(effective) || effective <= 0)
        throw new Error('WebAudio.PlaybackRateUnsupported: the effective float32 playback rate must be positive and finite.');
    return effective;
}

/** Bounded PCM queue scheduled on the audio clock, independent of frame and ended-event timing. */
export class WebAudioStream {
    constructor(context, destination, owner = null) {
        this.owner = owner ?? { context, loopBytes: 0 };
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
        this.looping = false;
        this.loopAudio = null;
        this.loopBytes = 0;
        this.loopLease = null;
        this.loopNode = null;
        this.loopOffset = 0;
        this.loopStarted = 0;
        this.reservedBytes = 0;
        this.retired = [];
        this.pending = null;
        this.closed = false;
    }

    entry(index) { return this.entries[(this.head + index) % capacity]; }

    handoffTime() {
        const quantum = (this.context.renderQuantumSize || 128) / this.context.sampleRate;
        return this.context.currentTime + 2 * quantum;
    }

    ensureIdle() {
        this.refresh();
        if (this.pending) throw new Error('WebAudio.StreamHandoffPending: retry this mutation after the native audio-clock handoff.');
    }

    preflightRetirement(count) {
        if (this.retired.length + count > maxRetiredNodes)
            throw new Error('WebAudio.StreamRetirementCapacityExceeded: too many native nodes await their audio-clock stop.');
    }

    detachNative(node) {
        if (!node) return;
        node.onended = null;
        try { node.stop(); } catch { /* Already stopped or never started. */ }
        node.disconnect();
    }

    finishRetired(record) {
        const index = this.retired.indexOf(record);
        if (index < 0) return;
        this.retired.splice(index, 1);
        if (record.node) {
            record.node.onended = null;
            record.node.disconnect();
            record.node = null;
        }
        if (record.lease) this.releaseLease(record.lease);
    }

    retireNode(node, when, lease = null) {
        if (!node) return;
        if (lease) this.retainLease(lease);
        const record = { node, lease, ended: false };
        this.retired.push(record);
        node.onended = () => {
            record.ended = true;
            // Ended only retires resources; playback and the logical clock are
            // advanced by the audio clock in refresh(), never by this event.
            if (this.pending && record.lease) {
                node.onended = null;
                node.disconnect();
                record.node = null;
            } else this.finishRetired(record);
        };
        try { node.stop(when); }
        catch { record.ended = true; if (!record.lease) this.finishRetired(record); }
    }

    commitPending() {
        const pending = this.pending;
        if (!pending || this.context.currentTime < pending.when) return;
        this.pending = null;
        if (pending.kind === 'loop') {
            if (pending.lease !== this.loopLease && this.loopLease) this.releaseLease(this.loopLease);
            this.looping = true;
            this.loopLease = pending.lease;
            this.loopAudio = pending.audio;
            this.loopBytes = pending.bytes;
            this.loopNode = pending.node;
            this.loopOffset = pending.offset;
            this.loopStarted = pending.when;
            this.rate = pending.rate;
            this.processed = 0;
            this.playing = true;
            this.stopped = false;
            if (pending.fromOneShots)
                for (let index = 0; index < this.count; index++) this.entry(index).offset = 0;
        } else if (pending.kind === 'oneShots') {
            if (this.loopLease) this.releaseLease(this.loopLease);
            this.looping = false;
            this.loopLease = null;
            this.loopAudio = null;
            this.loopBytes = 0;
            this.loopNode = null;
            this.loopOffset = 0;
            this.rate = pending.rate;
            this.processed = pending.target;
            this.entry(pending.target).offset = pending.offset;
            this.playing = true;
            this.stopped = false;
        } else if (pending.kind === 'stopAtEnd') {
            if (this.loopLease) this.releaseLease(this.loopLease);
            this.looping = false;
            this.loopLease = null;
            this.loopAudio = null;
            this.loopBytes = 0;
            this.loopNode = null;
            this.loopOffset = 0;
            this.rate = pending.rate;
            this.processed = this.count;
            this.playing = false;
            this.stopped = true;
        }
        for (let index = this.retired.length - 1; index >= 0; index--)
            if (this.retired[index].ended) this.finishRetired(this.retired[index]);
    }

    refresh() {
        this.commitPending();
        if (!this.playing || this.looping) return;
        const now = this.context.currentTime;
        while (this.processed < this.count && this.entry(this.processed).end <= now)
            this.releaseNode(this.entry(this.processed++));
        if (this.processed === this.count) { this.playing = false; this.stopped = true; }
    }

    contains(id) {
        for (let index = 0; index < this.count; index++)
            if (this.entry(index).id === id) return true;
        return false;
    }

    processedCount() {
        this.refresh();
        return this.pending?.kind === 'loop' ? 0 : this.processed;
    }

    /** The old and replacement aggregates both count toward the output's peak staging budget. */
    prepareLoop(ids = [], resolve = null, skip = 0) {
        let frames = 0;
        const count = this.count - skip + ids.length;
        for (let index = skip; index < this.count; index++) frames += this.entry(index).audio.length;
        for (let index = 0; index < ids.length; index++) frames += resolve(ids[index]).length;
        if (!count) return null;
        const format = skip < this.count ? this.entry(skip).audio : resolve(ids[0]);
        const bytes = frames * format.numberOfChannels * Float32Array.BYTES_PER_ELEMENT;
        if (!Number.isSafeInteger(bytes) || this.owner.loopBytes + bytes > maxLoopBytes)
            throw new Error('WebAudio.StreamLoopBudgetExceeded: aggregate and replacement float PCM exceed 8 MiB per output.');
        const audio = this.context.createBuffer(format.numberOfChannels, frames, format.sampleRate);
        let offset = 0;
        for (let index = skip; index < this.count; index++) {
            const part = this.entry(index).audio;
            for (let channel = 0; channel < part.numberOfChannels; channel++)
                part.copyFromChannel(audio.getChannelData(channel).subarray(offset, offset + part.length), channel);
            offset += part.length;
        }
        for (let index = 0; index < ids.length; index++) {
            const part = resolve(ids[index]);
            for (let channel = 0; channel < part.numberOfChannels; channel++)
                part.copyFromChannel(audio.getChannelData(channel).subarray(offset, offset + part.length), channel);
            offset += part.length;
        }
        return { audio, bytes };
    }

    retainLease(lease) {
        if (lease.refs++ !== 0) return;
        this.owner.loopBytes += lease.bytes;
        this.reservedBytes += lease.bytes;
    }

    releaseLease(lease) {
        if (--lease.refs !== 0) return;
        this.owner.loopBytes -= lease.bytes;
        this.reservedBytes -= lease.bytes;
    }

    installLoop(prepared) {
        const lease = { audio: prepared.audio, bytes: prepared.bytes, refs: 0 };
        this.retainLease(lease);
        if (this.loopLease) this.releaseLease(this.loopLease);
        this.loopLease = lease;
        this.loopAudio = prepared.audio;
        this.loopBytes = prepared.bytes;
    }

    discardLoop() {
        this.releaseLoopNode();
        if (this.loopLease) this.releaseLease(this.loopLease);
        this.loopLease = null;
        this.loopAudio = null;
        this.loopBytes = 0;
    }

    createLoopNode(audio) {
        const node = this.context.createBufferSource();
        try {
            node.buffer = audio;
            node.loop = true;
            node.playbackRate.value = this.rate;
            node.connect(this.destination);
        } catch (error) {
            this.detachNative(node);
            throw error;
        }
        return node;
    }

    releaseLoopNode() {
        if (!this.loopNode) return;
        this.detachNative(this.loopNode);
        this.loopNode = null;
    }

    cancelAllNative(keepLease = null) {
        this.detachNative(this.loopNode);
        for (let index = 0; index < this.count; index++) {
            const entry = this.entry(index);
            this.detachNative(entry.node);
            entry.node = null;
        }
        if (this.pending?.node) this.detachNative(this.pending.node);
        for (let index = 0; index < this.retired.length; index++)
            this.detachNative(this.retired[index].node);
        this.retired.length = 0;
        this.pending = null;
        this.loopNode = null;
        const keepBytes = keepLease?.bytes ?? 0;
        this.owner.loopBytes -= this.reservedBytes - keepBytes;
        this.reservedBytes = keepBytes;
        if (keepLease) keepLease.refs = 1;
    }

    clearRetiredNow() {
        for (let index = this.retired.length - 1; index >= 0; index--) {
            const record = this.retired[index];
            this.detachNative(record.node);
            this.finishRetired(record);
        }
    }

    enqueue(ids, resolve) {
        if (this.closed) throw new Error('WebAudio.StreamClosed.');
        if (!ids.length) return;
        this.ensureIdle();
        if (ids.length > capacity - this.count) throw new Error('WebAudio.StreamQueueCapacityExceeded: at most 32 buffers per source.');
        const first = this.count ? this.entry(0).audio : ids.length ? resolve(ids[0]) : null;
        for (let index = 0; index < ids.length; index++) {
            const audio = resolve(ids[index]);
            if (!audio) throw new Error('WebAudio.BufferEmpty: upload PCM before queuing.');
            if (audio.sampleRate !== first.sampleRate || audio.numberOfChannels !== first.numberOfChannels)
                throw new Error('WebAudio.StreamFormatMismatch: queued buffers must share their sample rate and channel count.');
        }
        const prepared = this.looping && ids.length ? this.prepareLoop(ids, resolve) : null;
        let replacement = null, when = 0, loopOffset = 0;
        if (this.looping && this.playing) {
            this.preflightRetirement(1);
            replacement = this.createLoopNode(prepared.audio);
            when = this.handoffTime();
            loopOffset = this.loopFrames(when) / this.loopAudio.sampleRate;
            try { replacement.start(when, loopOffset); }
            catch (error) { this.detachNative(replacement); throw error; }
        } else if (this.looping) loopOffset = this.queueOffset();
        const previousCount = this.count;
        for (let index = 0; index < ids.length; index++) {
            const entry = this.entry(this.count++);
            entry.id = ids[index];
            entry.audio = resolve(ids[index]);
            entry.offset = 0;
        }
        if (this.stopped) this.processed = this.count;
        if (this.looping) {
            if (replacement) {
                const lease = { audio: prepared.audio, bytes: prepared.bytes, refs: 0 };
                this.retainLease(lease);
                this.pending = { kind: 'loop', when, node: replacement, audio: prepared.audio,
                    bytes: prepared.bytes, lease, offset: loopOffset, rate: this.rate, fromOneShots: false };
                this.retireNode(this.loopNode, when, this.loopLease);
            } else {
                this.installLoop(prepared);
                this.loopOffset = loopOffset;
            }
        } else if (this.playing) {
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
        if (this.playing || this.pending || !this.count) return;
        if (this.processed === this.count) {
            if (this.looping) {
                this.processed = 0;
                this.loopOffset = 0;
                this.stopped = false;
            } else this.rewind();
        }
        this.rate = effectiveRate(rate);
        try {
            if (this.looping) {
                if (!this.loopAudio) this.installLoop(this.prepareLoop());
                const node = this.createLoopNode(this.loopAudio);
                const when = this.handoffTime();
                try { node.start(when, this.loopOffset); }
                catch (error) { this.detachNative(node); throw error; }
                this.loopNode = node;
                this.loopStarted = when;
                this.processed = 0;
            } else this.schedule(this.processed, this.context.currentTime);
            this.playing = true;
            this.stopped = false;
        } catch (error) { this.stop(); throw error; }
    }

    /** Queue-origin playback position in seconds. */
    queueOffset() {
        this.refresh();
        if (this.stopped) return 0;
        if (this.looping) return this.loopAudio ? this.loopFrames() / this.loopAudio.sampleRate : this.loopOffset;
        return this.nonLoopQueueOffsetAt(this.context.currentTime);
    }

    /** Modulo integer queue frames avoids rounding a sum of buffer durations. */
    loopFrames(at = this.context.currentTime) {
        if (!this.loopAudio) return this.count ? this.loopOffset * this.entry(0).audio.sampleRate : 0;
        const sampleRate = this.loopAudio.sampleRate;
        const frames = this.loopOffset * sampleRate
            + (this.playing ? Math.max(0, at - this.loopStarted) * this.rate * sampleRate : 0);
        return frames % this.loopAudio.length;
    }

    traversalEnd(at = this.context.currentTime) {
        const start = Math.max(at, this.loopStarted);
        return start + (this.loopAudio.length - this.loopFrames(start))
            / (this.rate * this.loopAudio.sampleRate);
    }

    stopAtTraversalEnd(when) {
        this.preflightRetirement(1);
        this.pending = { kind: 'stopAtEnd', when, rate: this.rate };
        this.retireNode(this.loopNode, when, this.loopLease);
    }

    nonLoopQueueOffsetAt(at) {
        if (this.stopped) return 0;
        let seconds = 0;
        for (let index = 0; index < this.count; index++) {
            const entry = this.entry(index);
            if (index < this.processed) { seconds += entry.audio.duration; continue; }
            if (!this.playing) return seconds + entry.offset;
            if (at >= entry.end) { seconds += entry.audio.duration; continue; }
            if (at <= entry.start) return seconds + entry.offset;
            return seconds + Math.min(entry.audio.duration, entry.offset + (at - entry.start) * this.rate);
        }
        return seconds;
    }

    offset() {
        this.refresh();
        if (this.looping) {
            let frames = this.loopFrames();
            for (let index = 0; index < this.count; index++) {
                const entry = this.entry(index);
                if (frames + 1e-7 < entry.audio.length) return frames / entry.audio.sampleRate;
                frames -= entry.audio.length;
            }
            return 0;
        }
        if (this.processed === this.count) return 0;
        const entry = this.entry(this.processed);
        return this.playing
            ? Math.min(entry.audio.duration, entry.offset + Math.max(0, this.context.currentTime - entry.start) * this.rate)
            : entry.offset;
    }

    currentIndex() {
        if (!this.looping) return this.processed;
        let frames = this.loopFrames();
        for (let index = 0; index < this.count; index++) {
            const length = this.entry(index).audio.length;
            if (frames + 1e-7 < length) return index;
            frames -= length;
        }
        return 0;
    }

    /** Seeks the retained queue; a paused source stays paused. */
    seek(seconds) {
        if (!Number.isFinite(seconds) || seconds < 0) throw new Error('WebAudio.InvalidOffset.');
        let duration = 0;
        for (let index = 0; index < this.count; index++) duration += this.entry(index).audio.duration;
        if (seconds >= duration) throw new Error('WebAudio.OffsetOutOfRange: the seek must remain within the retained queue.');
        this.ensureIdle();
        if (this.looping) {
            if (!this.loopAudio) this.installLoop(this.prepareLoop());
            if (this.playing) {
                this.preflightRetirement(1);
                const replacement = this.createLoopNode(this.loopAudio);
                const when = this.handoffTime();
                try { replacement.start(when, seconds); }
                catch (error) { this.detachNative(replacement); throw error; }
                this.pending = { kind: 'loop', when, node: replacement, audio: this.loopAudio,
                    bytes: this.loopBytes, lease: this.loopLease, offset: seconds, rate: this.rate, fromOneShots: false };
                this.retireNode(this.loopNode, when, this.loopLease);
                return;
            }
            this.loopOffset = seconds;
            this.stopped = false;
            this.processed = 0;
            return;
        }
        let target = 0, offset = seconds;
        while (offset >= this.entry(target).audio.duration) offset -= this.entry(target++).audio.duration;
        const wasPlaying = this.playing;
        this.stop();
        this.processed = target;
        this.entry(target).offset = offset;
        this.stopped = false;
        if (wasPlaying) this.play(this.rate);
    }

    sampleOffset() {
        this.refresh();
        if (!this.count || this.stopped) return 0;
        if (this.looping) {
            let frames = this.loopFrames();
            for (let index = 0; index < this.count; index++) {
                const entry = this.entry(index);
                if (frames + 1e-7 < entry.audio.length) return Math.floor(frames + 1e-7);
                frames -= entry.audio.length;
            }
            return 0;
        }
        const index = this.currentIndex();
        return Math.floor(this.offset() * this.entry(index).audio.sampleRate);
    }

    pause() {
        this.refresh();
        if (this.pending) {
            const pending = this.pending;
            const now = this.context.currentTime;
            if (pending.kind === 'loop') {
                const offset = this.looping ? this.loopFrames(now) / this.loopAudio.sampleRate
                    : this.nonLoopQueueOffsetAt(now);
                this.cancelAllNative(pending.lease);
                this.looping = true;
                this.loopLease = pending.lease;
                this.loopAudio = pending.audio;
                this.loopBytes = pending.bytes;
                this.loopOffset = offset % pending.audio.duration;
                this.rate = pending.rate;
                this.processed = 0;
                if (pending.fromOneShots)
                    for (let index = 0; index < this.count; index++) this.entry(index).offset = 0;
            } else {
                let target = 0, frames = this.loopFrames(now);
                while (target < this.count && frames + 1e-7 >= this.entry(target).audio.length)
                    frames -= this.entry(target++).audio.length;
                if (target === this.count) { target = 0; frames = 0; }
                this.cancelAllNative();
                this.looping = false;
                this.loopLease = null;
                this.loopAudio = null;
                this.loopBytes = 0;
                this.loopOffset = 0;
                this.processed = target;
                this.entry(target).offset = Math.max(0, frames) / this.entry(target).audio.sampleRate;
                this.rate = pending.rate;
            }
            this.playing = false;
            this.stopped = false;
            return;
        }
        if (!this.playing) return;
        if (this.looping) {
            this.loopOffset = this.queueOffset();
            this.releaseLoopNode();
        } else {
            const offset = this.offset();
            if (this.processed === this.count) return;
            this.entry(this.processed).offset = offset;
            for (let index = this.processed; index < this.count; index++) this.releaseNode(this.entry(index));
        }
        this.playing = false;
        this.clearRetiredNow();
    }

    setRate(rate) {
        rate = effectiveRate(rate);
        this.refresh();
        if (this.pending) {
            const pending = this.pending;
            if (rate === pending.rate) return;
            if (pending.kind === 'loop') {
                pending.node.playbackRate.value = rate;
                pending.rate = rate;
                return;
            }
            if (pending.kind === 'stopAtEnd') {
                const now = Math.max(this.context.currentTime, this.loopStarted);
                this.loopOffset = this.loopFrames(now) / this.loopAudio.sampleRate;
                this.loopStarted = now;
                this.loopNode.playbackRate.value = rate;
                this.rate = rate;
                pending.rate = rate;
                pending.when = this.traversalEnd(now);
                this.loopNode.stop(pending.when);
                return;
            }
            // Scheduled one-shot starts cannot move. Rebuild only this bounded
            // pending handoff, leaving the old loop and its clock unchanged.
            const nodes = new Array(this.count);
            try {
                for (let index = pending.target; index < this.count; index++) {
                    const node = this.context.createBufferSource();
                    nodes[index] = node;
                    node.buffer = this.entry(index).audio;
                    node.playbackRate.value = rate;
                    node.connect(this.destination);
                }
                if (this.context.currentTime >= pending.when) {
                    for (let index = pending.target; index < this.count; index++) this.detachNative(nodes[index]);
                    this.commitPending();
                    this.setRate(rate);
                    return;
                }
                let start = pending.when;
                for (let index = pending.target; index < this.count; index++) {
                    const entry = this.entry(index);
                    const partOffset = index === pending.target ? pending.offset : 0;
                    nodes[index].start(start, partOffset);
                    start += (entry.audio.duration - partOffset) / rate;
                }
            } catch (error) {
                for (let index = pending.target; index < this.count; index++) this.detachNative(nodes[index]);
                throw error;
            }
            let start = pending.when;
            for (let index = pending.target; index < this.count; index++) {
                const entry = this.entry(index);
                this.detachNative(entry.node);
                entry.node = nodes[index];
                const partOffset = index === pending.target ? pending.offset : 0;
                entry.start = start;
                entry.end = start + (entry.audio.duration - partOffset) / rate;
                start = entry.end;
            }
            pending.rate = rate;
            return;
        }
        if (rate === this.rate) return;
        if (this.looping && this.playing) {
            const when = Math.max(this.context.currentTime, this.loopStarted);
            this.loopOffset = this.loopFrames(when) / this.loopAudio.sampleRate;
            this.loopStarted = when;
            this.loopNode.playbackRate.value = rate;
            this.rate = rate;
            return;
        }
        const wasPlaying = this.playing;
        this.pause();
        this.rate = rate;
        if (wasPlaying && this.processed < this.count) this.play(rate);
    }

    setLooping(looping) {
        this.refresh();
        if (this.pending) {
            if ((this.pending.kind === 'loop') === looping) return;
            throw new Error('WebAudio.StreamHandoffPending: retry this mutation after the native audio-clock handoff.');
        }
        if (looping === this.looping) return;
        if (looping) {
            const prepared = this.count ? this.prepareLoop() : null;
            this.refresh();
            if (this.playing) {
                let oldNodes = 0;
                for (let index = 0; index < this.count; index++)
                    if (this.entry(index).node) oldNodes++;
                this.preflightRetirement(oldNodes);
                const replacement = this.createLoopNode(prepared.audio);
                const now = this.context.currentTime;
                const quantum = (this.context.renderQuantumSize || 128) / this.context.sampleRate;
                const when = Math.max(now + quantum,
                    Math.min(now + 2 * quantum, this.entry(this.count - 1).end));
                let offset = this.nonLoopQueueOffsetAt(when);
                if (offset >= prepared.audio.duration) offset = 0;
                try { replacement.start(when, offset); }
                catch (error) { this.detachNative(replacement); throw error; }
                const lease = { audio: prepared.audio, bytes: prepared.bytes, refs: 0 };
                this.retainLease(lease);
                this.pending = { kind: 'loop', when, node: replacement, audio: prepared.audio,
                    bytes: prepared.bytes, lease, offset, rate: this.rate, fromOneShots: true };
                for (let index = 0; index < this.count; index++) {
                    const entry = this.entry(index);
                    if (entry.node) { this.retireNode(entry.node, when); entry.node = null; }
                }
                return;
            }
            const offset = this.count ? this.queueOffset() : 0;
            if (prepared) this.installLoop(prepared);
            this.looping = true;
            this.loopOffset = offset;
            for (let index = 0; index < this.count; index++) this.entry(index).offset = 0;
            if (!this.stopped) this.processed = 0;
            return;
        }
        let target = 0, frames = this.loopFrames();
        while (target < this.count && frames + 1e-7 >= this.entry(target).audio.length)
            frames -= this.entry(target++).audio.length;
        if (target === this.count && this.count) { target = 0; frames = 0; }
        if (this.count && !this.stopped) {
            if (this.playing) {
                const end = this.traversalEnd();
                if (end <= this.handoffTime()) { this.stopAtTraversalEnd(end); return; }
                this.preflightRetirement(1);
                const nodes = new Array(this.count);
                try {
                    for (let index = 0; index < this.count; index++) {
                        const node = this.context.createBufferSource();
                        nodes[index] = node;
                        node.buffer = this.entry(index).audio;
                        node.playbackRate.value = this.rate;
                        node.connect(this.destination);
                    }
                    const when = this.handoffTime();
                    if (end <= when) {
                        for (let index = 0; index < nodes.length; index++) this.detachNative(nodes[index]);
                        this.stopAtTraversalEnd(end);
                        return;
                    }
                    frames = this.loopFrames(when);
                    target = 0;
                    while (target < this.count && frames + 1e-7 >= this.entry(target).audio.length)
                        frames -= this.entry(target++).audio.length;
                    if (target === this.count) { target = 0; frames = 0; }
                    const offset = Math.max(0, frames) / this.entry(target).audio.sampleRate;
                    let start = when;
                    for (let index = target; index < this.count; index++) {
                        const entry = this.entry(index);
                        const partOffset = index === target ? offset : 0;
                        nodes[index].start(start, partOffset);
                        entry.node = nodes[index];
                        entry.start = start;
                        entry.end = start + (entry.audio.duration - partOffset) / this.rate;
                        start = entry.end;
                    }
                    for (let index = 0; index < target; index++) this.detachNative(nodes[index]);
                    this.pending = { kind: 'oneShots', when, target, offset, rate: this.rate };
                    this.retireNode(this.loopNode, when, this.loopLease);
                    return;
                }
                catch (error) {
                    for (let index = 0; index < nodes.length; index++) this.detachNative(nodes[index]);
                    for (let index = 0; index < this.count; index++) this.entry(index).node = null;
                    throw error;
                }
            }
            this.entry(target).offset = Math.max(0, frames) / this.entry(target).audio.sampleRate;
            this.processed = target;
        }
        this.discardLoop();
        this.looping = false;
        this.loopOffset = 0;
    }

    releaseNode(entry) {
        if (!entry.node) return;
        try { entry.node.stop(); } catch { /* A naturally finished node is already stopped. */ }
        entry.node.disconnect();
        entry.node = null;
    }

    stop() {
        const requestedLooping = this.pending ? this.pending.kind === 'loop' : this.looping;
        this.cancelAllNative();
        this.playing = false;
        this.stopped = true;
        this.looping = requestedLooping;
        this.loopLease = null;
        this.loopAudio = null;
        this.loopBytes = 0;
        for (let index = 0; index < this.count; index++) {
            const entry = this.entry(index);
            entry.offset = 0;
        }
        this.loopOffset = 0;
        this.processed = this.count;
    }

    rewind() { this.stop(); this.processed = 0; this.stopped = false; }

    unqueue(output, maximum = output.length) {
        this.refresh();
        if (this.pending) return 0;
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
        if (count && this.looping) this.discardLoop();
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
