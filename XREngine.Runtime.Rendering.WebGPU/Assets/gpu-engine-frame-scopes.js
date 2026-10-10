const receiptCapacity = 64;

/** Fixed owner-local receipts; only WebGPU/Promise objects are created on an ordinary frame. */
export class GpuEngineFrameScopes {
    constructor(renderer) {
        this.renderer = renderer;
        this.disposed = false;
        this.completedSequence = 0;
        this.trackCompletion = false;
        this.receipts = new Array(receiptCapacity);
        this.stats = { capacity: receiptCapacity, pending: 0, peakPending: 0, started: 0, completed: 0,
            validationErrors: 0, outOfMemoryErrors: 0, rejectedScopes: 0, obsoleteErrors: 0,
            capacityFailures: 0, admissionDeferrals: 0, webGpuPromises: 0, observerPromises: 0, captureGatePromises: 0,
            queueCompletionPromises: 0, completionPolls: 0 };
        for (let index = 0; index < receiptCapacity; index++)
            this.receipts[index] = new FrameScopeReceipt(this);
    }

    /** Check before entering the synchronous managed frame, never after accepting its bytes. */
    canBegin() {
        this.renderer._requireOwner();
        if (this.disposed) throw new Error('WebGPU.EngineFrame.ScopesDisposed: renderer receipts are retired.');
        if (this.stats.pending < receiptCapacity) return true;
        this.stats.admissionDeferrals++;
        return false;
    }

    begin(sequence, capture) {
        if (this.disposed) throw new Error('WebGPU.EngineFrame.ScopesDisposed: renderer receipts are retired.');
        for (let index = 0; index < this.receipts.length; index++) {
            const receipt = this.receipts[index];
            if (receipt.active) continue;
            receipt.begin(sequence, capture);
            return receipt;
        }
        this.stats.capacityFailures++;
        throw new Error('WebGPU.EngineFrame.ScopeCapacity: 64 frame validation receipts remain pending.');
    }

    pollCompletedSequence() {
        this.renderer._requireOwner();
        this.trackCompletion = true;
        this.stats.completionPolls++;
        return this.completedSequence;
    }

    /** Retains validation and completion for the exact just-accepted offscreen producer. */
    getAcceptedProducerGate(sequence) {
        this.renderer._requireOwner();
        if (!Number.isSafeInteger(sequence) || sequence <= 0 || this.disposed)
            throw new Error('WebGPU.SceneCapture.InvalidProducer: a live accepted frame sequence is required.');
        for (const receipt of this.receipts) {
            if (!receipt.active || !receipt.closed || !receipt.submitted || receipt.context.sequence !== sequence)
                continue;
            if (!receipt.gate) {
                receipt.gate = new Promise(receipt.captureExecutor);
                this.stats.captureGatePromises++;
            }
            return receipt.gate;
        }
        throw new Error('WebGPU.SceneCapture.ProducerReceiptUnavailable: readback must retain its exact producer before yielding.');
    }

    dispose() { this.disposed = true; }
}

class FrameScopeReceipt {
    constructor(pool) {
        this.pool = pool;
        this.active = false;
        this.closed = true;
        this.submitted = false;
        this.device = null;
        this.scopeCount = 0;
        this.remaining = 0;
        this.error = null;
        this.gate = null;
        this.resolveGate = null;
        this.context = { stage: 'engine-frame-validation', label: 'Engine frame', commandIndex: -1,
            drawIndex: -1, owner: 0, generation: 0, sequence: 0 };
        // All observer/executor functions are allocated with the receipt, never with a frame.
        this.validationResult = error => this.settle(error, false, 'engine-frame-validation');
        this.validationRejected = error => this.settle(error, true, 'engine-frame-validation');
        this.memoryResult = error => this.settle(error, false, 'engine-frame-out-of-memory');
        this.memoryRejected = error => this.settle(error, true, 'engine-frame-out-of-memory');
        this.queueResult = () => {
            const pool = this.pool, r = pool.renderer;
            if (!pool.disposed && !r._disposed && r.device === this.device && r._owner === this.context.owner)
                pool.completedSequence = Math.max(pool.completedSequence, this.context.sequence);
            this.settle(null, false, 'engine-frame-completion');
        };
        this.queueRejected = error => this.settle(error, true, 'engine-frame-completion');
        this.captureExecutor = resolve => { this.resolveGate = resolve; };
    }

    begin(sequence, capture) {
        const r = this.pool.renderer, stats = this.pool.stats;
        this.active = true;
        this.closed = false;
        this.submitted = false;
        this.device = r.device;
        this.error = null;
        this.context.owner = r._owner;
        this.context.generation = r._generation;
        this.context.sequence = sequence;
        stats.started++;
        stats.pending++;
        stats.peakPending = Math.max(stats.peakPending, stats.pending);
        try {
            this.device.pushErrorScope('out-of-memory');
            this.scopeCount = 1;
            this.device.pushErrorScope('validation');
            this.scopeCount = 2;
            if (capture) {
                // The cold capture consumer accepts an error value as well as a rejection.
                // Resolving avoids an unobserved rejection if submission throws before capture.
                this.gate = new Promise(this.captureExecutor);
                stats.captureGatePromises++;
            }
        } catch (error) {
            this.close();
            throw error;
        }
    }

    close(submitted = false) {
        const gate = this.gate;
        if (this.closed) return gate;
        this.submitted = submitted;
        submitted = submitted && this.pool.trackCompletion;
        this.closed = true;
        const scopes = this.scopeCount;
        this.remaining = scopes + (submitted ? 1 : 0);
        this.scopeCount = 0;
        // Pop both scopes in this synchronous stack, before any preparation/capture can
        // resume. Resource preparation follows the same pop-before-await contract.
        if (scopes === 2) this.pop(true);
        if (scopes > 0) this.pop(false);
        if (submitted) {
            try {
                const promise = this.device.queue.onSubmittedWorkDone();
                this.pool.stats.queueCompletionPromises++;
                this.pool.stats.webGpuPromises++;
                promise.then(this.queueResult, this.queueRejected);
                this.pool.stats.observerPromises++;
            } catch (error) { this.queueRejected(error); }
        } else if (scopes === 0 && this.active) this.finish();
        return gate;
    }

    pop(validation) {
        const stats = this.pool.stats;
        try {
            const promise = this.device.popErrorScope();
            stats.webGpuPromises++;
            // Promise.then necessarily creates its result promise; account for it rather
            // than claiming a zero-allocation JavaScript or browser-runtime frame.
            promise.then(validation ? this.validationResult : this.memoryResult,
                validation ? this.validationRejected : this.memoryRejected);
            stats.observerPromises++;
        } catch (error) {
            this.settle(error, true, validation ? 'engine-frame-validation' : 'engine-frame-out-of-memory');
        }
    }

    settle(error, rejected, stage) {
        const pool = this.pool, r = pool.renderer, stats = pool.stats;
        if (error || rejected) {
            if (rejected) stats.rejectedScopes++;
            else if (stage === 'engine-frame-validation') stats.validationErrors++;
            else stats.outOfMemoryErrors++;
            this.error ??= error?.message ? error : new Error(String(error));
            if (!pool.disposed && !r._disposed && r.device === this.device && r._owner === this.context.owner) {
                this.context.stage = stage;
                r._recordError(error, stage, this.context.label, this.context);
                r._fail(error);
            } else stats.obsoleteErrors++;
        }
        if (--this.remaining === 0) this.finish();
    }

    finish() {
        this.resolveGate?.(this.error);
        this.resolveGate = null;
        this.gate = null;
        this.error = null;
        this.device = null;
        this.active = false;
        this.pool.stats.pending--;
        this.pool.stats.completed++;
    }
}
