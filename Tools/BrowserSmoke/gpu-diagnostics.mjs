/** Browser-process evidence only; unavailable CDP metadata cannot replace engine acceptance. */
export async function captureGpuProcessState(browser, report, stage) {
    const snapshot = { stage, time: Date.now() };
    (report.gpuProcessSnapshots ??= []).push(snapshot);
    let session, timer, finished = false;
    const detach = async value => {
        let cleanupTimer;
        try {
            await Promise.race([value.detach(), new Promise(resolve => { cleanupTimer = setTimeout(resolve, 500); })]);
        } catch { /* Closing the owned browser also releases an unresponsive CDP session. */ }
        finally { clearTimeout(cleanupTimer); }
    };
    try {
        const capture = async () => {
            const attached = await browser.newBrowserCDPSession();
            // Session creation can complete after the metadata deadline; never leave
            // that late session attached while the remaining qualification runs.
            if (finished) { await detach(attached); return null; }
            session = attached;
            return Promise.all([session.send('SystemInfo.getInfo'), session.send('SystemInfo.getProcessInfo')]);
        };
        const values = await Promise.race([
            capture(),
            new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('GPU process snapshot exceeded 4500 ms.')), 4500); }),
        ]);
        snapshot.gpu = values[0].gpu;
        snapshot.processes = values[1].processInfo;
    } catch (error) { snapshot.error = String(error).slice(0, 2048); }
    finally {
        finished = true;
        clearTimeout(timer);
        if (session) await detach(session);
    }
}

/** Executed in a separate real browser page, without loading engine or managed runtime code. */
export async function initializeGpuCanary({ manifestUrl, budgetMs }) {
    const result = { scope: 'Independent WebGPU diagnostics only; never engine acceptance.',
        stage: 'request-adapter', stages: [], submittedFrames: 0, draws: 0,
        firstError: null, deviceLoss: null, explicitDestroyRequested: false };
    let rejectFailure, device, context, depth;
    const failed = new Promise((_, reject) => { rejectFailure = reject; });
    failed.catch(() => {});
    const fail = error => {
        if (!result.firstError) {
            result.firstError = { stage: result.stage, message: String(error?.message ?? error).slice(0, 2048),
                stack: String(error?.stack ?? '').slice(0, 4096) };
            rejectFailure(new Error(result.firstError.message));
        }
    };
    const bounded = async operation => {
        let timer;
        try {
            return await Promise.race([operation, failed, new Promise((_, reject) => {
                timer = setTimeout(() => reject(new Error(`GPU canary ${result.stage} exceeded ${budgetMs} ms.`)), budgetMs);
            })]);
        } finally { clearTimeout(timer); }
    };
    const snapshot = () => JSON.parse(JSON.stringify(result));
    const dispose = () => {
        result.explicitDestroyRequested = true;
        context?.unconfigure();
        depth?.destroy();
        device?.destroy();
    };
    window.gpuCanary = { snapshot, dispose };
    try {
        if (!navigator.gpu) throw new Error('GPU canary requires navigator.gpu.');
        const adapter = await bounded(navigator.gpu.requestAdapter());
        if (!adapter) throw new Error('GPU canary requestAdapter returned null.');
        const info = adapter.info ?? {};
        result.adapter = { vendor: info.vendor ?? '', architecture: info.architecture ?? '',
            device: info.device ?? '', description: info.description ?? '', fallback: adapter.isFallbackAdapter ?? null };
        result.stage = 'request-device';
        device = await bounded(adapter.requestDevice());
        device.addEventListener('uncapturederror', event => fail(event.error));
        device.lost.then(info => {
            if (result.explicitDestroyRequested) return;
            result.deviceLoss = { reason: info.reason, message: String(info.message).slice(0, 2048), stage: result.stage,
                explicitDestroyRequested: false };
            fail(new Error(`GPU canary device lost (${info.reason}): ${info.message}`));
        }, fail);
        result.stage = 'configure-canvas';
        const canvas = document.querySelector('canvas');
        context = canvas.getContext('webgpu');
        if (!context) throw new Error('GPU canary canvas context is unavailable.');
        const format = navigator.gpu.getPreferredCanvasFormat();
        context.configure({ device, format, alphaMode: 'opaque' });
        depth = device.createTexture({ size: [128, 128], format: 'depth24plus', usage: GPUTextureUsage.RENDER_ATTACHMENT });
        const depthView = depth.createView();
        const frames = async pipeline => {
            for (let frame = 0; frame < 12; frame++) {
                await bounded(new Promise(requestAnimationFrame));
                const encoder = device.createCommandEncoder();
                const pass = encoder.beginRenderPass({ colorAttachments: [{ view: context.getCurrentTexture().createView(),
                    clearValue: { r: 0.2, g: 0.3, b: 0.4, a: 1 }, loadOp: 'clear', storeOp: 'store' }],
                    depthStencilAttachment: { view: depthView, depthClearValue: 1, depthLoadOp: 'clear', depthStoreOp: 'discard' } });
                if (pipeline) { pass.setPipeline(pipeline); pass.draw(3); result.draws++; }
                pass.end();
                device.queue.submit([encoder.finish()]);
                result.submittedFrames++;
            }
            await bounded(device.queue.onSubmittedWorkDone());
        };
        window.gpuCanary.runStage = async stage => {
            result.stage = stage;
            try {
                if (stage === 'clear') await frames(null);
                else if (stage === 'triangle') {
                    const shader = device.createShaderModule({ label: 'Independent diagnostic triangle', code: `
@vertex fn vertexMain(@builtin(vertex_index) index: u32) -> @builtin(position) vec4f {
    let positions = array<vec2f, 3>(vec2f(-1.0, -1.0), vec2f(3.0, -1.0), vec2f(-1.0, 3.0));
    return vec4f(positions[index], 0.5, 1.0);
}
@fragment fn fragmentMain() -> @location(0) vec4f { return vec4f(0.25, 0.5, 0.75, 1.0); }` });
                    const compilation = await bounded(shader.getCompilationInfo());
                    const errors = compilation.messages.filter(message => message.type === 'error');
                    if (errors.length) throw new Error(errors.map(message => message.message).join('\n'));
                    const pipeline = await bounded(device.createRenderPipelineAsync({ layout: 'auto',
                        vertex: { module: shader, entryPoint: 'vertexMain' },
                        fragment: { module: shader, entryPoint: 'fragmentMain', targets: [{ format }] },
                        primitive: { topology: 'triangle-list' },
                        depthStencil: { format: 'depth24plus', depthWriteEnabled: true, depthCompare: 'always' } }));
                    await frames(pipeline);
                } else if (stage === 'cooked-wgsl') {
                    const read = async url => {
                        const response = await bounded(fetch(url));
                        if (!response.ok) throw new Error(`GPU canary fetch failed: ${response.status}`);
                        return response;
                    };
                    const manifest = await bounded((await read(manifestUrl)).json());
                    const artifact = manifest.artifacts?.find(value => value.name === 'engine-depth-probe');
                    if (!artifact) throw new Error('GPU canary requires the same engine-depth-probe artifact.');
                    const descriptorUrl = new URL(artifact.descriptor, manifestUrl);
                    const descriptor = await bounded((await read(descriptorUrl)).json());
                    const bytes = await bounded((await read(new URL(descriptor.source.url, descriptorUrl))).arrayBuffer());
                    if (bytes.byteLength > 1048576) throw new Error('GPU canary cooked WGSL exceeds 1 MiB.');
                    const digest = await bounded(crypto.subtle.digest('SHA-256', bytes));
                    const hash = [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('');
                    if (hash !== descriptor.source.sha256 || bytes.byteLength !== descriptor.source.byteLength)
                        throw new Error('GPU canary cooked WGSL hash or byte length does not match its descriptor.');
                    result.cookedArtifact = { name: artifact.name, sourceHash: hash, bytes: bytes.byteLength };
                    const shader = device.createShaderModule({ label: 'Independent cooked depth probe', code: new TextDecoder().decode(bytes) });
                    const compilation = await bounded(shader.getCompilationInfo());
                    const errors = compilation.messages.filter(message => message.type === 'error');
                    if (errors.length) throw new Error(errors.map(message => message.message).join('\n'));
                    // This diagnostic's known ABI is deliberately separate from engine translation.
                    const bindings = device.createBindGroupLayout({ entries: [0, 1].map(binding => ({ binding,
                        visibility: GPUShaderStage.VERTEX, buffer: { type: 'uniform', hasDynamicOffset: true, minBindingSize: 64 } })) });
                    await bounded(device.createRenderPipelineAsync({ layout: device.createPipelineLayout({ bindGroupLayouts: [bindings] }),
                        vertex: { module: shader, entryPoint: descriptor.entryPoints.vertex,
                            buffers: [{ arrayStride: 12, attributes: [{ shaderLocation: 0, offset: 0, format: 'float32x3' }] }] },
                        fragment: { module: shader, entryPoint: descriptor.entryPoints.fragment, targets: [{ format }] },
                        primitive: { topology: 'triangle-list', frontFace: 'ccw', cullMode: 'none' },
                        depthStencil: { format: 'depth24plus', depthWriteEnabled: true, depthCompare: 'less' } }));
                    await bounded(device.queue.onSubmittedWorkDone());
                } else throw new Error('Unknown GPU canary stage.');
                if (result.firstError) throw new Error(result.firstError.message);
                result.stages.push({ stage, status: 'passed', time: Date.now() });
                return snapshot();
            } catch (error) { fail(error); throw error; }
        };
        return snapshot();
    } catch (error) { fail(error); throw error; }
}
