// This executor draws one diagnostic triangle per view. Mesh packets are outside its API.
const maximumViews = 4;

function asError(value) {
    return value instanceof Error ? value : new Error(value?.message ?? String(value));
}

export class WebGpuCanvasRenderer {
    constructor(canvas, onState, onFailure) {
        this.canvas = canvas;
        this.onState = onState;
        this.onFailure = onFailure;
        this._generation = 0;
        this._disposed = false;
        this._failed = false;
        this._deviceLost = false;
        this._configured = false;
        this._width = 0;
        this._height = 0;
        this._matrix = new Float32Array(16);
        this._dynamicOffsets = [0];
        this._submission = [null];
        this._colorAttachment = { view: undefined, loadOp: 'clear', storeOp: 'store', clearValue: { r: 0, g: 0, b: 0, a: 1 } };
        this._renderPassDescriptor = { colorAttachments: [this._colorAttachment] };
        this._onUncapturedError = event => this._fail(event.error);
    }

    get maxDimension() {
        return this.device?.limits.maxTextureDimension2D ?? 0;
    }

    get generation() {
        return this._generation;
    }

    _assertActive(signal, lateDevice) {
        if (signal?.aborted || this._disposed || this._failed || this._deviceLost) {
            lateDevice?.destroy();
            if (signal?.aborted || (this._disposed && !this._failed))
                throw new DOMException('WebGPU initialization was canceled.', 'AbortError');
            throw new Error('WebGPU device is unavailable.');
        }
    }

    _fail(reason) {
        if (this._disposed || this._failed) return;
        this._failed = true;
        const error = asError(reason);
        this.dispose();
        try { this.onFailure(error); } catch (callbackError) { console.error(callbackError); }
    }

    async initialize(signal) {
        if (this._disposed || this._failed || this.device || this._initializing)
            throw new Error('WebGPU renderer cannot be initialized again.');
        this._initializing = true;
        try {
            this._assertActive(signal);
            if (!globalThis.isSecureContext || !navigator.gpu)
                throw new Error('WebGPU requires a secure context and navigator.gpu.');
            this.onState('requesting-adapter');
            const adapter = await navigator.gpu.requestAdapter();
            this._assertActive(signal);
            if (!adapter) throw new Error('No WebGPU adapter is available.');

            this.onState('requesting-device');
            const device = await adapter.requestDevice();
            this._assertActive(signal, device);
            this.device = device;
            device.addEventListener('uncapturederror', this._onUncapturedError);
            // Attach both handlers immediately so device loss cannot produce an unhandled rejection.
            device.lost.then(info => {
                if (this._disposed) return; // Our own device.destroy() is expected.
                this._deviceLost = true;
                this._fail(new Error(`WebGPU device lost (${info.reason}): ${info.message}`));
            }, error => this._fail(error));

            this.context = this.canvas.getContext('webgpu');
            if (!this.context) throw new Error('WebGPU canvas context is unavailable.');
            this.format = navigator.gpu.getPreferredCanvasFormat();
            const alignment = device.limits.minUniformBufferOffsetAlignment;
            this._uniformStride = Math.ceil(64 / alignment) * alignment;
            this.uniformBuffer = device.createBuffer({
                size: this._uniformStride * maximumViews,
                usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
            });
            const bindGroupLayout = device.createBindGroupLayout({ entries: [{
                binding: 0,
                visibility: GPUShaderStage.VERTEX,
                buffer: { type: 'uniform', hasDynamicOffset: true, minBindingSize: 64 },
            }] });
            this.bindGroup = device.createBindGroup({ layout: bindGroupLayout, entries: [{
                binding: 0, resource: { buffer: this.uniformBuffer, size: 64 },
            }] });

            this.onState('creating-pipeline');
            const response = await fetch(new URL('./triangle.wgsl', import.meta.url), { signal });
            this._assertActive(signal);
            if (!response.ok) throw new Error(`WGSL request failed: HTTP ${response.status}.`);
            const source = await response.text();
            this._assertActive(signal);
            const shader = device.createShaderModule({ code: source });
            const diagnostics = await shader.getCompilationInfo();
            this._assertActive(signal);
            const errors = diagnostics.messages.filter(message => message.type === 'error');
            for (const message of diagnostics.messages) {
                if (message.type === 'warning')
                    console.warn(`WGSL ${message.lineNum}:${message.linePos}: ${message.message}`);
            }
            if (errors.length)
                throw new Error(`WGSL compilation failed: ${errors.map(message => `${message.lineNum}:${message.linePos} ${message.message}`).join('; ')}`);
            const pipeline = await device.createRenderPipelineAsync({
                layout: device.createPipelineLayout({ bindGroupLayouts: [bindGroupLayout] }),
                vertex: { module: shader, entryPoint: 'vertexMain' },
                fragment: { module: shader, entryPoint: 'fragmentMain', targets: [{ format: this.format }] },
                primitive: { topology: 'triangle-list' },
            });
            this._assertActive(signal);
            this.pipeline = pipeline;
            this.onState('ready');
        } catch (error) {
            if (error?.name === 'AbortError' && (signal?.aborted || this._disposed))
                this.dispose();
            else
                this._fail(error);
            throw error;
        } finally {
            this._initializing = false;
        }
    }

    resize(width, height) {
        if (!this.pipeline || this._disposed) throw new Error('WebGPU renderer is not ready.');
        const limit = this.maxDimension;
        if (!Number.isInteger(width) || !Number.isInteger(height) || width < 0 || height < 0 || width > limit || height > limit)
            throw new RangeError(`Canvas dimensions must be integers from 0 to ${limit}.`);
        if (width === this._width && height === this._height) return this._generation;
        if (this._generation === 0x7fffffff)
            throw new Error('Canvas generations are exhausted; restart the session.');
        this.abortFrame();
        this._width = width;
        this._height = height;
        try {
            this.canvas.width = width;
            this.canvas.height = height;
            if (this._configured) this.context.unconfigure();
            this._configured = false;
            if (width > 0 && height > 0) {
                this.context.configure({ device: this.device, format: this.format, alphaMode: 'opaque' });
                this._configured = true;
            }
            this._generation++;
            return this._generation;
        } catch (error) {
            this._fail(error);
            throw error;
        }
    }

    beginFrame(generation) {
        if (this._disposed) throw new Error('The WebGPU renderer is disposed.');
        if (generation !== this._generation) throw new Error('The frame targets an obsolete canvas generation.');
        if (this.pass) throw new Error('A WebGPU frame is already active.');
        if (!this._configured) return false;
        try {
            // WebGPU creates fresh texture/view, encoder and pass objects for each submitted frame.
            this._colorAttachment.view = this.context.getCurrentTexture().createView();
            this.encoder = this.device.createCommandEncoder();
            this.pass = this.encoder.beginRenderPass(this._renderPassDescriptor);
            this.pass.setPipeline(this.pipeline);
            this._drawnViews = 0;
            return true;
        } catch (error) {
            this._fail(error);
            throw error;
        }
    }

    draw(viewIndex, x, y, width, height,
        m11, m12, m13, m14, m21, m22, m23, m24,
        m31, m32, m33, m34, m41, m42, m43, m44) {
        if (!this.pass) throw new Error('No WebGPU frame is active.');
        if (!Number.isInteger(viewIndex) || viewIndex < 0 || viewIndex >= maximumViews || (this._drawnViews & (1 << viewIndex)))
            throw new RangeError('Each frame supports one draw for each of four view indices.');
        if (!Number.isInteger(x) || !Number.isInteger(y) || !Number.isInteger(width) || !Number.isInteger(height)
            || width <= 0 || height <= 0 || x < 0 || y < 0 || x + width > this._width || y + height > this._height)
            throw new RangeError('Viewport must be an integer rectangle within the canvas.');
        // C# Matrix4x4 row-major fields become WGSL columns in this order: the
        // resulting transpose is the column-vector transform used by the shader.
        const matrix = this._matrix;
        matrix[0] = m11; matrix[1] = m12; matrix[2] = m13; matrix[3] = m14;
        matrix[4] = m21; matrix[5] = m22; matrix[6] = m23; matrix[7] = m24;
        matrix[8] = m31; matrix[9] = m32; matrix[10] = m33; matrix[11] = m34;
        matrix[12] = m41; matrix[13] = m42; matrix[14] = m43; matrix[15] = m44;
        try {
            const offset = viewIndex * this._uniformStride;
            this.device.queue.writeBuffer(this.uniformBuffer, offset, matrix);
            this._dynamicOffsets[0] = offset;
            this.pass.setBindGroup(0, this.bindGroup, this._dynamicOffsets);
            this.pass.setViewport(x, y, width, height, 0, 1);
            this.pass.setScissorRect(x, y, width, height);
            this.pass.draw(3);
            this._drawnViews |= 1 << viewIndex;
        } catch (error) {
            this._fail(error);
            throw error;
        }
    }

    endFrame() {
        if (!this.pass) return false;
        try {
            this.pass.end();
            // finish() creates a command buffer; neither it nor the canvas view survives this frame.
            this._submission[0] = this.encoder.finish();
            this.device.queue.submit(this._submission);
            return true;
        } catch (error) {
            this._fail(error);
            throw error;
        } finally {
            this._submission[0] = null;
            this.pass = undefined;
            this.encoder = undefined;
            this._colorAttachment.view = undefined;
        }
    }

    abortFrame() {
        if (this.pass) {
            try { this.pass.end(); } catch { /* A discarded encoder is never submitted. */ }
        }
        this.pass = undefined;
        this.encoder = undefined;
        this._colorAttachment.view = undefined;
        this._submission[0] = null;
    }

    dispose() {
        if (this._disposed) return;
        this._disposed = true;
        this.abortFrame();
        if (this._configured) {
            try { this.context.unconfigure(); } catch (error) { console.error(error); }
        }
        this._configured = false;
        try { this.uniformBuffer?.destroy(); } catch (error) { console.error(error); }
        try { this.device?.removeEventListener('uncapturederror', this._onUncapturedError); } catch (error) { console.error(error); }
        try { this.device?.destroy(); } catch (error) { console.error(error); }
        this.bindGroup = undefined;
        this.pipeline = undefined;
        this.uniformBuffer = undefined;
        this.device = undefined;
        this.context = undefined;
    }
}
