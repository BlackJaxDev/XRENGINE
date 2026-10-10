/** Observes bounded Advanced creation and ordinary GPU submission calls. */
export function installAdvancedSubmissionObservation() {
    const maximumCreationRecords = 128;
    const stats = { compute: {}, raster: {}, readMappings: 0, creation: {
        computePipelines: { calls: 0, omitted: 0, records: [] },
        shaderModules: { calls: 0, omitted: 0, records: [] },
    } };
    globalThis.advancedSubmissionEvidence = stats;
    globalThis.advancedCanvasFailureEvidence = null;
    globalThis.advancedSubmissionSnapshot = () => {
        const pipelines = stats.creation.computePipelines;
        const now = performance.now();
        return { ...stats, creation: { ...stats.creation, computePipelines: {
            calls: pipelines.calls, omitted: pipelines.omitted,
            records: pipelines.records.map(({ startedAtMs, ...record }) => ({
                ...record, elapsedMs: record.elapsedMs ?? now - startedAtMs,
            })),
        } } };
    };
    document.addEventListener('xrengine-canvas-failed', event => {
        globalThis.advancedCanvasFailureEvidence = event.detail;
    });
    const label = descriptor => {
        try { return String(descriptor?.label ?? '').slice(0, 128); }
        catch { return ''; }
    };
    const createComputePipelineAsync = GPUDevice.prototype.createComputePipelineAsync;
    GPUDevice.prototype.createComputePipelineAsync = function (...args) {
        const startedAtMs = performance.now();
        const evidence = stats.creation.computePipelines;
        evidence.calls++;
        let promise;
        try { promise = createComputePipelineAsync.apply(this, args); }
        catch (error) {
            if (evidence.records.length < maximumCreationRecords)
                evidence.records.push({ label: label(args[0]), status: 'rejected',
                    elapsedMs: performance.now() - startedAtMs });
            else evidence.omitted++;
            throw error;
        }
        if (evidence.records.length < maximumCreationRecords) {
            const record = { label: label(args[0]), status: 'pending', elapsedMs: null, startedAtMs };
            evidence.records.push(record);
            // Observe settlement without changing the promise returned to the renderer.
            try { void promise.then(() => {
                record.status = 'fulfilled'; record.elapsedMs = performance.now() - startedAtMs;
            }, () => {
                record.status = 'rejected'; record.elapsedMs = performance.now() - startedAtMs;
            }); } catch { /* Instrumentation must not change the original result. */ }
        } else evidence.omitted++;
        return promise;
    };
    const createShaderModule = GPUDevice.prototype.createShaderModule;
    GPUDevice.prototype.createShaderModule = function (...args) {
        const evidence = stats.creation.shaderModules;
        evidence.calls++;
        let module;
        try { module = createShaderModule.apply(this, args); }
        catch (error) {
            if (evidence.records.length < maximumCreationRecords)
                evidence.records.push({ label: label(args[0]), status: 'rejected', codeBytes: null,
                    sha256: null });
            else evidence.omitted++;
            throw error;
        }
        if (evidence.records.length < maximumCreationRecords) {
            const record = { label: label(args[0]), status: 'pending', codeBytes: null, sha256: null };
            evidence.records.push(record);
            let code;
            try { code = args[0]?.code; }
            catch { record.status = 'unavailable'; return module; }
            if (typeof code !== 'string') { record.status = 'unavailable'; return module; }
            // Hash asynchronously; no shader source is stored in report evidence.
            void Promise.resolve().then(async () => {
                const bytes = new TextEncoder().encode(code);
                record.codeBytes = bytes.byteLength;
                const digest = await crypto.subtle.digest('SHA-256', bytes);
                record.sha256 = [...new Uint8Array(digest)]
                    .map(byte => byte.toString(16).padStart(2, '0')).join('');
                record.status = 'fulfilled';
            }).catch(() => { record.status = 'unavailable'; });
        } else evidence.omitted++;
        return module;
    };
    const pipelines = new WeakMap();
    function trackPass(prototype, counters, operations) {
        const setPipeline = prototype.setPipeline;
        prototype.setPipeline = function (pipeline) {
            const result = setPipeline.call(this, pipeline);
            pipelines.set(this, pipeline.label);
            return result;
        };
        for (const operation of operations) {
            const original = prototype[operation];
            prototype[operation] = function (...args) {
                const result = original.apply(this, args);
                const label = pipelines.get(this) ?? '';
                counters[label] = (counters[label] ?? 0) + 1;
                return result;
            };
        }
    }
    trackPass(GPUComputePassEncoder.prototype, stats.compute, ['dispatchWorkgroups', 'dispatchWorkgroupsIndirect']);
    trackPass(GPURenderPassEncoder.prototype, stats.raster, ['draw', 'drawIndexed', 'drawIndirect', 'drawIndexedIndirect']);
    const mapAsync = GPUBuffer.prototype.mapAsync;
    GPUBuffer.prototype.mapAsync = function (mode, ...args) {
        if ((mode & GPUMapMode.READ) !== 0) stats.readMappings++;
        return mapAsync.call(this, mode, ...args);
    };
}
