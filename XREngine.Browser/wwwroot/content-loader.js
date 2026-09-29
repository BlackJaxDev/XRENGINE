import { CONTENT_LIMITS, contentManifestUrl, validateContentManifest } from './content-manifest.js';

function check(signal) { signal.throwIfAborted(); }

function pause(milliseconds, signal, frame = false) {
    check(signal);
    return new Promise((resolve, reject) => {
        let ticket;
        const aborted = () => {
            if (frame) cancelAnimationFrame(ticket); else clearTimeout(ticket);
            reject(signal.reason);
        };
        const finished = () => {
            signal.removeEventListener('abort', aborted);
            resolve();
        };
        signal.addEventListener('abort', aborted, { once: true });
        ticket = frame ? requestAnimationFrame(finished) : setTimeout(finished, milliseconds);
    });
}

function deadline(parent, label) {
    const controller = new AbortController();
    const aborted = () => controller.abort(parent.reason);
    parent.addEventListener('abort', aborted, { once: true });
    if (parent.aborted) aborted();
    const timer = setTimeout(() => controller.abort(new Error(`Cooked content ${label}: request timed out.`)), CONTENT_LIMITS.requestMilliseconds);
    return {
        signal: controller.signal,
        close() { clearTimeout(timer); parent.removeEventListener('abort', aborted); },
    };
}

/** One package load owns at most three bounded payload buffers; source bytes are discarded after upload. */
export class BrowserContentLoader {
    static async open(url, capabilities, signal, onProgress = null) {
        const loader = new BrowserContentLoader(contentManifestUrl(url), signal, onProgress);
        try {
            const bytes = await loader._request(loader.manifestUrl, null, CONTENT_LIMITS.manifestBytes, 'manifest', 'no-cache');
            let value;
            try { value = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)); }
            catch (error) { throw new Error(`Cooked content manifest is not valid UTF-8 JSON: ${error.message}`); }
            finally { loader._release(bytes); }
            check(loader.signal);
            loader._package = validateContentManifest(value, loader.manifestUrl, capabilities);
            loader.manifest = loader._package.manifest;
            loader._statistics.selectedBytes = loader._package.selectedBytes;
            loader._statistics.selectedAssets = loader._package.essential.length + loader._package.streamed.length;
            loader._statistics.state = 'ready';
            loader._progress();
            return loader;
        } catch (error) {
            loader.dispose();
            throw error;
        }
    }

    constructor(manifestUrl, signal, onProgress) {
        this.manifestUrl = manifestUrl;
        this._controller = new AbortController();
        this.signal = this._controller.signal;
        this._parent = signal;
        this._abortParent = () => this._controller.abort(signal.reason);
        if (signal) {
            signal.addEventListener('abort', this._abortParent, { once: true });
            if (signal.aborted) this._abortParent();
        }
        this._onProgress = onProgress;
        this._busy = false;
        this._essentialComplete = false;
        this._consumed = new Set();
        this._statistics = {
            state: 'manifest', selectedAssets: 0, selectedBytes: 0,
            downloadedAssets: 0, consumedAssets: 0, receivedDecodedBytes: 0,
            downloadedCompressedTextureBytes: 0, consumedCompressedTextureBytes: 0,
            downloadedUncompressedPayloadBytes: 0, consumedUncompressedPayloadBytes: 0,
            decodedEquivalentTextureBytes: 0,
            currentStagingBytes: 0, peakStagingBytes: 0, activeRequests: 0,
            retries: 0, lastAsset: null, lastError: null,
        };
    }

    async consumeEssential(consume) {
        await this._consume(this._package.essential, consume, 'essential');
        this._essentialComplete = true;
    }

    async consumeStreamed(consume) {
        if (!this._essentialComplete) throw new Error('Essential content must be consumed before streaming.');
        await this._consume(this._package.streamed, consume, 'streaming');
    }

    getStatistics() { return { ...this._statistics }; }

    dispose() {
        this._controller.abort(new Error('Cooked content load was disposed.'));
        this._parent?.removeEventListener('abort', this._abortParent);
        this._onProgress = null;
        this._statistics.state = 'disposed';
    }

    _progress() { this._onProgress?.(this.getStatistics()); }

    _reserve(bytes) {
        if (this._statistics.currentStagingBytes + bytes > CONTENT_LIMITS.stagingBytes)
            throw new Error('Cooked content staging byte budget exceeded.');
        this._statistics.currentStagingBytes += bytes;
        this._statistics.peakStagingBytes = Math.max(this._statistics.peakStagingBytes, this._statistics.currentStagingBytes);
    }

    _release(bytes) { this._statistics.currentStagingBytes -= bytes._stagingAllocation ?? bytes.byteLength; }

    async _request(url, expectedBytes, limit, label, cache) {
        const budget = deadline(this.signal, label);
        try {
            for (let attempt = 0; attempt < CONTENT_LIMITS.attempts; attempt++) {
                check(budget.signal);
                let buffer = null;
                let reader = null;
                let response = null;
                let retryable = false;
                this._statistics.activeRequests++;
                try {
                    try {
                        response = await fetch(url, {
                            signal: budget.signal, redirect: 'error', credentials: 'omit',
                            mode: 'same-origin', cache,
                        });
                    } catch (error) { retryable = error instanceof TypeError; throw error; }
                    check(budget.signal);
                    if (!response.ok) {
                        retryable = response.status === 408 || response.status === 429 || response.status >= 500;
                        await response.body?.cancel();
                        throw new Error(`HTTP ${response.status}`);
                    }
                    if (!response.body) throw new Error('Response has no readable body.');
                    const declared = response.headers.get('content-length');
                    // Encoded responses report wire length, not the decoded stream length.
                    if (declared !== null && !response.headers.get('content-encoding')) {
                        const length = Number(declared);
                        if (!Number.isSafeInteger(length) || length < 0 || length > limit
                            || (expectedBytes !== null && length !== expectedBytes))
                            throw new Error('Response length does not match the content budget.');
                    }
                    const allocation = expectedBytes ?? limit;
                    this._reserve(allocation);
                    buffer = new Uint8Array(allocation);
                    reader = response.body.getReader();
                    let used = 0;
                    while (true) {
                        let result;
                        try { result = await reader.read(); }
                        catch (error) { retryable = error instanceof TypeError; throw error; }
                        check(budget.signal);
                        if (result.done) break;
                        this._statistics.receivedDecodedBytes += result.value.byteLength;
                        if (used + result.value.byteLength > allocation) throw new Error('Response exceeded its declared byte budget.');
                        buffer.set(result.value, used);
                        used += result.value.byteLength;
                    }
                    if (expectedBytes !== null && used !== expectedBytes) throw new Error('Response ended before its declared byte length.');
                    if (expectedBytes === null) {
                        const result = buffer.subarray(0, used);
                        // Keep the full allocation accounted for until JSON decoding finishes.
                        Object.defineProperty(result, '_stagingAllocation', { value: buffer.byteLength });
                        return result;
                    }
                    return buffer;
                } catch (error) {
                    if (buffer) this._release(buffer);
                    if (budget.signal.aborted) throw budget.signal.reason;
                    if (!retryable || attempt + 1 === CONTENT_LIMITS.attempts)
                        throw new Error(`Cooked content ${label}: ${error.message}`);
                    this._statistics.retries++;
                } finally {
                    if (reader) {
                        try { await reader.cancel(); } catch { /* Preserve the request error. */ }
                        reader.releaseLock();
                    } else if (response?.body) {
                        try { await response.body.cancel(); } catch { /* The response may already be aborted. */ }
                    }
                    this._statistics.activeRequests--;
                }
                await pause(250 * 2 ** attempt, budget.signal);
            }
            throw new Error(`Cooked content ${label}: retry budget exhausted.`);
        } finally { budget.close(); }
    }

    async _download(asset) {
        const variant = this._package.selected.get(asset.id);
        const bytes = await this._request(this._package.payloadUrls.get(variant), variant.bytes,
            variant.bytes, asset.id, 'force-cache');
        try {
            check(this.signal);
            if (!globalThis.crypto?.subtle) throw new Error('SHA-256 verification requires a secure browser context.');
            const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
            check(this.signal);
            let hash = '';
            for (const byte of digest) hash += byte.toString(16).padStart(2, '0');
            if (hash !== variant.hash) throw new Error(`Cooked content ${asset.id}: SHA-256 integrity mismatch.`);
            this._statistics.downloadedAssets++;
            if (asset.kind === 'texture' && variant.requiredFeatures.length)
                this._statistics.downloadedCompressedTextureBytes += bytes.byteLength;
            else this._statistics.downloadedUncompressedPayloadBytes += bytes.byteLength;
            this._progress();
            return { asset, variant, bytes };
        } catch (error) {
            this._release(bytes);
            throw error;
        }
    }

    async _consume(order, consume, state) {
        check(this.signal);
        if (this._busy) throw new Error('A cooked content consumption pass is already active.');
        if (typeof consume !== 'function') throw new Error('A cooked asset consumer is required.');
        this._busy = true;
        this._statistics.state = state;
        const pending = [];
        let next = 0;
        const startNext = () => {
            while (next < order.length && this._consumed.has(order[next].id)) next++;
            if (next < order.length) {
                // Rejections are captured immediately, including failures in prefetched resources.
                pending.push(this._download(order[next++]).then(value => ({ value }), error => ({ error })));
            }
        };
        try {
            for (let index = 0; index < CONTENT_LIMITS.concurrency; index++) startNext();
            while (pending.length) {
                const result = await pending.shift();
                if (result.error) throw result.error;
                const { asset, variant, bytes } = result.value;
                try {
                    check(this.signal);
                    // One bounded upload per animation frame keeps resource integration out of a long loading task.
                    await pause(0, this.signal, true);
                    check(this.signal);
                    try { await consume(asset, variant, bytes); }
                    catch (error) {
                        check(this.signal);
                        throw new Error(`Cooked content ${asset.id}: ${error?.message ?? String(error)}`);
                    }
                    check(this.signal);
                    this._consumed.add(asset.id);
                    this._statistics.consumedAssets++;
                    if (asset.kind === 'texture' && variant.requiredFeatures.length)
                        this._statistics.consumedCompressedTextureBytes += bytes.byteLength;
                    else this._statistics.consumedUncompressedPayloadBytes += bytes.byteLength;
                    if (asset.kind === 'texture') {
                        // This is an RGBA8 equivalent, not a decoded allocation; blocks upload directly.
                        for (let level = 0; level < variant.mipByteLengths.length; level++)
                            this._statistics.decodedEquivalentTextureBytes += 4
                                * Math.max(1, Math.floor(variant.width / 2 ** level))
                                * Math.max(1, Math.floor(variant.height / 2 ** level));
                    }
                    this._statistics.lastAsset = asset.id;
                } finally { this._release(bytes); }
                this._progress();
                startNext();
            }
            this._statistics.state = state === 'essential' ? 'essential-ready' : 'complete';
            this._progress();
        } catch (error) {
            this._statistics.lastError = error?.message ?? String(error);
            this._statistics.state = this.signal.aborted ? 'cancelled' : 'failed';
            this._controller.abort(error);
            this._progress();
            throw error;
        } finally {
            // Cancellation drains all bounded fetches before a recovery session may replace this loader.
            for (const task of pending) {
                const result = await task;
                if (result.value) this._release(result.value.bytes);
            }
            this._busy = false;
        }
    }
}
