import { contentManifestUrl } from './content-manifest.js';
import { selectBrowserSubmissionStrategy } from './webgpu/browser-submission-strategy.js';

/** Desktop publishing replaces the development descriptor; a broken deployment never silently starts the demo. */
export async function loadBrowserPublishConfig(signal) {
    const controller = new AbortController();
    const abort = () => controller.abort(signal.reason);
    signal.addEventListener('abort', abort, { once: true });
    if (signal.aborted) abort();
    const timer = setTimeout(() => controller.abort(new Error('Browser launch descriptor timed out.')), 30000);
    let reader;
    try {
        const url = new URL('./browser-publish.json', import.meta.url);
        const response = await fetch(url, { signal: controller.signal, cache: 'no-cache',
            credentials: 'omit', redirect: 'error', mode: 'same-origin' });
        if (!response.ok || !response.body)
            throw new Error(`Browser launch descriptor could not be loaded (HTTP ${response.status}).`);
        const bytes = new Uint8Array(4096);
        reader = response.body.getReader();
        let used = 0;
        while (true) {
            const result = await reader.read();
            controller.signal.throwIfAborted();
            if (result.done) break;
            if (used + result.value.byteLength > bytes.length)
                throw new Error('Browser launch descriptor exceeds 4096 bytes.');
            bytes.set(result.value, used);
            used += result.value.byteLength;
        }
        const value = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, used)));
        const fields = ['schema', 'world', 'quality', 'submissionStrategy', 'skinning'];
        if (!value || typeof value !== 'object' || Array.isArray(value)
            || Object.keys(value).length !== fields.length || Object.keys(value).some(key => !fields.includes(key))
            || value.schema !== 1 || !['low', 'balanced', 'high'].includes(value.quality)
            || !['Cpu', 'Compute'].includes(value.skinning))
            throw new Error('Browser launch descriptor has an unsupported schema or settings.');
        selectBrowserSubmissionStrategy(value.submissionStrategy);
        if (typeof value.submissionStrategy !== 'string')
            throw new Error('Browser launch descriptor requires an explicit submission strategy.');
        if (value.world !== null) value.world = contentManifestUrl(value.world, url.href);
        else if (value.skinning === 'Compute')
            throw new Error('Published compute deformation requires an explicit cooked world.');
        return Object.freeze(value);
    } finally {
        if (reader) {
            try { await reader.cancel(); } catch { /* Preserve the load failure. */ }
            reader.releaseLock();
        }
        clearTimeout(timer);
        signal.removeEventListener('abort', abort);
    }
}
