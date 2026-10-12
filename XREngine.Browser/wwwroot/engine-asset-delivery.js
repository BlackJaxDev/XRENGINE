// One browser event loop owns all source admissions. A lease may not span a managed await.
export const INTEGRATION_LIMITS = Object.freeze({ startsPerFrame: 2, bytesPerFrame: 2 * 1024 * 1024,
    millisecondsPerFrame: 4, pendingItems: 32, pendingBytes: 12 * 1024 * 1024 });
const queue = [];
let active = null, frameRequest = 0, frameStart = 0, starts = 0, bytes = 0, pendingBytes = 0;

function scheduleFrame() {
    if (frameRequest || active || !queue.length) return;
    frameRequest = requestAnimationFrame(() => {
        frameRequest = 0;
        frameStart = performance.now();
        starts = bytes = 0;
        admit();
    });
}

function admit() {
    if (active || !queue.length) return;
    const next = queue[0];
    // One indivisible oversized batch may start in an otherwise empty frame.
    // Time limits govern additional starts; they cannot preempt deserialization.
    if (starts && (starts >= INTEGRATION_LIMITS.startsPerFrame
        || bytes + next.bytes > INTEGRATION_LIMITS.bytesPerFrame
        || performance.now() - frameStart >= INTEGRATION_LIMITS.millisecondsPerFrame)) {
        scheduleFrame();
        return;
    }
    active = queue.shift();
    starts++;
    bytes += active.bytes;
    active.started = performance.now();
    active.resolve();
}

/** Cancellable FIFO admission, shared across every source and every hydration caller. */
export function queueIntegration(owner, byteLength) {
    if (!Number.isSafeInteger(byteLength) || byteLength < 1 || queue.length + (active ? 1 : 0) >= INTEGRATION_LIMITS.pendingItems
        || pendingBytes + byteLength > INTEGRATION_LIMITS.pendingBytes)
        throw new Error('AssetSource.IntegrationQueueBudgetExceeded.');
    const item = { owner, bytes: byteLength, started: null, resolve: null, reject: null, closed: false };
    item.promise = new Promise((resolve, reject) => { item.resolve = resolve; item.reject = reject; });
    item.promise.catch(() => {});
    queue.push(item);
    pendingBytes += byteLength;
    scheduleFrame();
    return item;
}

/** Releases an admission only after its synchronous managed work and rollback are finished. */
export function finishIntegration(item) {
    if (item.closed) return 0;
    item.closed = true;
    pendingBytes -= item.bytes;
    const elapsed = item.started === null ? 0 : Math.max(0, performance.now() - item.started);
    if (active === item) {
        active = null;
        // Continuations run after the disposed managed publication scope has unwound.
        queueMicrotask(admit);
    } else {
        const index = queue.indexOf(item);
        if (index >= 0) queue.splice(index, 1);
        item.reject(new Error('AssetSource.IntegrationCancelled.'));
    }
    if (!queue.length && frameRequest) { cancelAnimationFrame(frameRequest); frameRequest = 0; }
    return elapsed;
}

export function deliverySnapshot(source) {
    const transport = source.loader.getStatistics();
    const descriptor = source.descriptorLoader?.getStatistics();
    const receivedDecodedBytes = transport.receivedDecodedBytes + (descriptor?.receivedDecodedBytes ?? source.descriptorReceivedBytes);
    let verifiedAssets = 0, verifiedBytes = 0, essentialVerifiedAssets = 0, essentialVerifiedBytes = 0;
    let retainedAssets = 0, retainedObjects = 0, retainedSerializedBytes = 0, retainedManagedEstimateBytes = 0;
    for (const entry of source.assets?.values() ?? []) {
        if (entry.verified) {
            verifiedAssets++; verifiedBytes += entry.bytes;
            if (entry.essential) { essentialVerifiedAssets++; essentialVerifiedBytes += entry.bytes; }
        }
        if (entry.retained) {
            retainedAssets++; retainedObjects += entry.retained.objects;
            retainedSerializedBytes += entry.retained.serializedBytes;
            retainedManagedEstimateBytes += entry.retained.managedBytes;
        }
    }
    let integrationQueued = 0, integrating = 0, managedStagingBytes = 0;
    for (const item of source.integrations.values()) {
        if (item.started === null) integrationQueued++;
        else { integrating++; managedStagingBytes += item.bytes - (item.managedCompanionBytes ?? 0); }
    }
    return {
        state: source.deliveryState, selectedAssets: source.assets?.size ?? 0, selectedSerializedBytes: source.selectedBytes ?? 0,
        essentialAssets: source.essentialAssets ?? 0, essentialSerializedBytes: source.essentialBytes ?? 0,
        verifiedAssets, verifiedBytes, essentialVerifiedAssets, essentialVerifiedBytes,
        verificationBasis: 'Unique runtime delivery reads after catalog admission; package preflight verification is counted separately as transfer.',
        retainedAssets, retainedObjects, retainedSerializedBytes, retainedManagedEstimateBytes, retainedNativeEstimateBytes: source.retainedNativeEstimateBytes,
        retainedManagedEstimateBasis: 'Retained hydration allocation estimate: cumulative allocations during each retained synchronous asset hydration, including temporaries; excludes metadata/shader catalogs and later mutations.',
        retainedNativeEstimateBasis: 'Owned texture mip and buffer DataSource lengths at hydration; excludes allocator overhead, other native libraries and Jolt.',
        nativeHeapBytes: null, estimatedGpuBytes: null,
        receivedDecodedBytes, preflightReceivedDecodedBytes: source.deliveryState === 'opening' ? receivedDecodedBytes : source.preflightReceivedBytes,
        retries: transport.retries + (descriptor?.retries ?? source.descriptorRetries),
        activeRequests: transport.activeRequests + (descriptor?.activeRequests ?? 0), queuedReads: source.queue.length,
        javascriptStagingBytes: transport.currentStagingBytes + (descriptor?.currentStagingBytes ?? 0),
        peakJavascriptStagingBytes: Math.max(transport.peakStagingBytes, descriptor?.peakStagingBytes ?? source.descriptorPeakStagingBytes),
        managedIntegrationStagingBytes: managedStagingBytes, managedCompanionStagingBytes: source.heldManagedBytes, integrationQueued, integrating,
        integrationsCompleted: source.integrationsCompleted, integrationMilliseconds: source.integrationMilliseconds,
        longestIntegrationMilliseconds: source.longestIntegrationMilliseconds, overBudgetIntegrations: source.overBudgetIntegrations,
        integrationLimits: INTEGRATION_LIMITS, failedReads: source.failedReads, cancelledReads: source.cancelledReads,
        lastError: source.lastError,
    };
}
