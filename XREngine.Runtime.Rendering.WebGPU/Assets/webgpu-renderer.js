import { GpuResourceTable } from './gpu-resource-table.js';
import { drawRecordBytes, maximumDraws, packetHeaderBytes, validateFramePacket } from '../frame-packet.js';
import { loadBrowserUnlitArtifact, shaderDeviceRequirements } from './shader-artifact.js';

const maximumUploadBytes = 64 * 1024 * 1024;
const maximumTextureBytes = 64 * 1024 * 1024;
const whitePixel = new Uint8Array([255, 255, 255, 255]);

function asError(value) {
    return value instanceof Error ? value : new Error(value?.message ?? String(value));
}

function byteLengthOf(memory) {
    const length = memory?.byteLength;
    if (!Number.isSafeInteger(length) || length < 0)
        throw new TypeError('A byte-oriented .NET memory view is required.');
    return length;
}

function copyMemory(memory, length) {
    const bytes = new Uint8Array(length);
    memory.copyTo(bytes);
    return bytes;
}

/** One device, one canvas surface, and one generation-stamped resource namespace. */
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
        this._owner = 0;
        this._arenaGeneration = 0;
        this._frameSequence = 0;
        this._executing = false;
        this._resources = new GpuResourceTable();
        this._retired = new Set();
        this._packetBytes = new Uint8Array(packetHeaderBytes);
        this._packetView = new DataView(this._packetBytes.buffer);
        this._uniformCapacity = 16;
        this._dynamicOffsets = [0];
        this._submission = [null];
        this._colorAttachment = { view: undefined, loadOp: 'clear', storeOp: 'store', clearValue: { r: 0, g: 0, b: 0, a: 1 } };
        this._depthAttachment = { view: undefined, depthClearValue: 1, depthLoadOp: 'clear', depthStoreOp: 'discard' };
        this._renderPassDescriptor = { colorAttachments: [this._colorAttachment], depthStencilAttachment: this._depthAttachment };
        this._stats = { packets: 0, draws: 0, copiedBytes: 0, uploadedBytes: 0, arenaGrowth: 0, rejectedPackets: 0, controlCalls: 0 };
        this._shaderArtifact = undefined;
        this._onUncapturedError = event => this._fail(event.error);
    }

    get maxDimension() {
        return this.device?.limits.maxTextureDimension2D ?? 0;
    }

    get isDeviceLost() {
        return this._deviceLost;
    }

    get generation() {
        return this._generation;
    }

    get colorFormat() {
        return this.format;
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

    _requireOwner() {
        if (!this.device || !this.pipeline || this._disposed || this._failed || !this._owner)
            throw new Error('WebGPU renderer has no active session owner.');
        if (this._executing) throw new Error('Resources cannot change during packet execution.');
    }

    setOwner(session) {
        this._stats.controlCalls++;
        if (!this.pipeline || this._disposed || this._owner || !Number.isInteger(session)
            || session <= 0 || session > 0x7fffffff)
            throw new Error('A ready renderer can be assigned one positive session owner.');
        this._owner = session;
    }

    _newUniformBuffer(capacity) {
        return this.device.createBuffer({
            size: this._uniformStride * capacity,
            usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
        });
    }

    _newViewBindGroup(buffer) {
        return this.device.createBindGroup({ layout: this._viewLayout, entries: [{
            binding: 0, resource: { buffer, size: 64 },
        }] });
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

            this.onState('loading-shader');
            const artifact = await loadBrowserUnlitArtifact(signal);
            this._assertActive(signal);
            const deviceRequirements = shaderDeviceRequirements(adapter, artifact.descriptor, artifact.artifactIdentity);
            this._shaderArtifact = {
                identity: artifact.artifactIdentity,
                requiredFeatures: [...deviceRequirements.requiredFeatures],
                requiredLimits: { ...deviceRequirements.requiredLimits },
            };
            this.onState('requesting-device');
            const device = await adapter.requestDevice(deviceRequirements);
            this._assertActive(signal, device);
            this.device = device;
            device.addEventListener('uncapturederror', this._onUncapturedError);
            device.lost.then(info => {
                if (this._disposed) return;
                this._deviceLost = true;
                this._fail(new Error(`WebGPU device lost (${info.reason}): ${info.message}`));
            }, error => this._fail(error));

            this.context = this.canvas.getContext('webgpu');
            if (!this.context) throw new Error('WebGPU canvas context is unavailable.');
            this.format = navigator.gpu.getPreferredCanvasFormat();
            const alignment = device.limits.minUniformBufferOffsetAlignment;
            this._uniformStride = Math.ceil(64 / alignment) * alignment;
            this._uniformBytes = new Uint8Array(this._uniformCapacity * this._uniformStride);
            this._uniformView = new DataView(this._uniformBytes.buffer);
            this.uniformBuffer = this._newUniformBuffer(this._uniformCapacity);
            this._viewLayout = device.createBindGroupLayout({ entries: [{
                binding: 0, visibility: GPUShaderStage.VERTEX,
                buffer: { type: 'uniform', hasDynamicOffset: true, minBindingSize: 64 },
            }] });
            this.bindGroup = this._newViewBindGroup(this.uniformBuffer);
            this._materialLayout = device.createBindGroupLayout({ entries: [
                { binding: 0, visibility: GPUShaderStage.FRAGMENT, buffer: { type: 'uniform', minBindingSize: 16 } },
                { binding: 1, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float', viewDimension: '2d' } },
                { binding: 2, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'filtering' } },
            ] });
            this._sampler = device.createSampler({ minFilter: 'linear', magFilter: 'linear', mipmapFilter: 'linear', addressModeU: 'clamp-to-edge', addressModeV: 'clamp-to-edge' });
            this._whiteTexture = device.createTexture({ size: [1, 1], format: 'rgba8unorm-srgb', usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST });
            this._whiteView = this._whiteTexture.createView();
            device.queue.writeTexture({ texture: this._whiteTexture }, whitePixel, { bytesPerRow: 4 }, [1, 1]);

            this.onState('creating-pipeline');
            const shader = device.createShaderModule({ code: artifact.source });
            const diagnostics = await shader.getCompilationInfo();
            this._assertActive(signal);
            const errors = diagnostics.messages.filter(message => message.type === 'error');
            const shaderLabel = `${artifact.descriptor.source.path} (${artifact.artifactIdentity})`;
            for (const message of diagnostics.messages) {
                if (message.type === 'warning')
                    console.warn(`${shaderLabel}:${message.lineNum}:${message.linePos}: ${message.message}`);
            }
            if (errors.length)
                throw new Error(`WGSL compilation failed: ${errors.map(message => `${shaderLabel}:${message.lineNum}:${message.linePos} ${message.message}`).join('; ')}`);
            this.pipeline = await device.createRenderPipelineAsync({
                layout: device.createPipelineLayout({ bindGroupLayouts: [this._viewLayout, this._materialLayout] }),
                vertex: { module: shader, entryPoint: artifact.descriptor.entryPoints.vertex, buffers: [{ arrayStride: 20, attributes: [
                    { shaderLocation: 0, offset: 0, format: 'float32x3' },
                    { shaderLocation: 1, offset: 12, format: 'float32x2' },
                ] }] },
                fragment: { module: shader, entryPoint: artifact.descriptor.entryPoints.fragment, targets: [{ format: this.format }] },
                primitive: { topology: 'triangle-list', cullMode: 'none' },
                depthStencil: { format: 'depth24plus', depthWriteEnabled: true, depthCompare: 'less' },
            });
            this._assertActive(signal);
            this.onState('ready');
        } catch (error) {
            if (error?.name === 'AbortError' && (signal?.aborted || this._disposed)) this.dispose();
            else this._fail(error);
            throw error;
        } finally {
            this._initializing = false;
        }
    }

    _retire(resource) {
        if (!resource) return;
        const device = this.device;
        this._retired.add(resource);
        device.queue.onSubmittedWorkDone().then(() => {
            this._retired.delete(resource);
            resource.destroy();
        }, () => {
            this._retired.delete(resource);
            resource.destroy();
        });
    }

    resize(width, height) {
        this._stats.controlCalls++;
        if (!this.pipeline || this._disposed) throw new Error('WebGPU renderer is not ready.');
        if (this._executing) throw new Error('The surface cannot resize during packet execution.');
        const limit = this.maxDimension;
        if (!Number.isInteger(width) || !Number.isInteger(height) || width < 0 || height < 0 || width > limit || height > limit)
            throw new RangeError(`Canvas dimensions must be integers from 0 to ${limit}.`);
        if (width === this._width && height === this._height) return this._generation;
        if (this._generation === 0x7fffffff) throw new Error('Canvas generations are exhausted; restart the session.');
        this._width = width;
        this._height = height;
        try {
            this.canvas.width = width;
            this.canvas.height = height;
            if (this._configured) this.context.unconfigure();
            this._configured = false;
            const oldDepth = this.depthTexture;
            this.depthTexture = undefined;
            this.depthView = undefined;
            this._retire(oldDepth);
            if (width > 0 && height > 0) {
                this.context.configure({ device: this.device, format: this.format, alphaMode: 'opaque' });
                this.depthTexture = this.device.createTexture({ size: [width, height], format: 'depth24plus', usage: GPUTextureUsage.RENDER_ATTACHMENT });
                this.depthView = this.depthTexture.createView();
                this._configured = true;
            }
            this._generation++;
            return this._generation;
        } catch (error) {
            this._fail(error);
            throw error;
        }
    }

    createMesh(vertexMemory, indexMemory) {
        this._stats.controlCalls++;
        this._requireOwner();
        const vertexBytes = byteLengthOf(vertexMemory);
        const indexBytes = byteLengthOf(indexMemory);
        if (!vertexBytes || vertexBytes > maximumUploadBytes || vertexBytes % 20
            || indexBytes < 12 || indexBytes > maximumUploadBytes || indexBytes % 12
            || vertexBytes > this.device.limits.maxBufferSize || indexBytes > this.device.limits.maxBufferSize)
            throw new RangeError('Mesh vertex or triangle index bytes are invalid.');
        const vertices = copyMemory(vertexMemory, vertexBytes);
        const indices = copyMemory(indexMemory, indexBytes);
        this._stats.copiedBytes += vertexBytes + indexBytes;
        const vertexData = new DataView(vertices.buffer);
        const indexData = new DataView(indices.buffer);
        const vertexCount = vertexBytes / 20;
        if (vertexCount < 3) throw new RangeError('A mesh requires at least three vertices.');
        for (let offset = 0; offset < vertexBytes; offset += 20) {
            for (let component = 0; component < 5; component++) {
                if (!Number.isFinite(vertexData.getFloat32(offset + component * 4, true)))
                    throw new RangeError('Mesh vertices must be finite.');
            }
        }
        for (let offset = 0; offset < indexBytes; offset += 4) {
            if (indexData.getUint32(offset, true) >= vertexCount)
                throw new RangeError('Mesh index exceeds the vertex count.');
        }
        let vertexBuffer, indexBuffer;
        try {
            vertexBuffer = this.device.createBuffer({ size: vertexBytes, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST });
            indexBuffer = this.device.createBuffer({ size: indexBytes, usage: GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST });
            this.device.queue.writeBuffer(vertexBuffer, 0, vertices);
            this.device.queue.writeBuffer(indexBuffer, 0, indices);
            const handle = this._resources.add('mesh', { vertexBuffer, indexBuffer, indexCount: indexBytes / 4 }, this._owner);
            this._stats.uploadedBytes += vertexBytes + indexBytes;
            return handle;
        } catch (error) {
            this._retire(vertexBuffer);
            this._retire(indexBuffer);
            throw error;
        }
    }

    createTexture(width, height, rgbaMemory) {
        this._stats.controlCalls++;
        this._requireOwner();
        const bytes = width * height * 4;
        if (!Number.isInteger(width) || !Number.isInteger(height) || width <= 0 || height <= 0
            || width > this.maxDimension || height > this.maxDimension
            || !Number.isSafeInteger(bytes) || bytes > maximumTextureBytes || byteLengthOf(rgbaMemory) !== bytes)
            throw new RangeError('RGBA8 texture dimensions or byte count are invalid.');
        const pixels = copyMemory(rgbaMemory, bytes);
        this._stats.copiedBytes += bytes;
        let texture;
        try {
            texture = this.device.createTexture({ size: [width, height], format: 'rgba8unorm-srgb', usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST });
            this.device.queue.writeTexture({ texture }, pixels, { bytesPerRow: width * 4, rowsPerImage: height }, [width, height]);
            const handle = this._resources.add('texture', { texture, view: texture.createView(), references: 0 }, this._owner);
            this._stats.uploadedBytes += bytes;
            return handle;
        } catch (error) {
            this._retire(texture);
            throw error;
        }
    }

    createMaterial(textureHandle, r, g, b, a) {
        this._stats.controlCalls++;
        this._requireOwner();
        if (![r, g, b, a].every(value => Number.isFinite(value) && value >= 0 && value <= 1) || a !== 1)
            throw new RangeError('Opaque material tint must have linear channels in [0, 1] and alpha 1.');
        const texture = textureHandle === 0 ? null : this._resources.getHandle(textureHandle, 'texture', this._owner);
        let colorBuffer;
        try {
            colorBuffer = this.device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
            this.device.queue.writeBuffer(colorBuffer, 0, new Float32Array([r, g, b, a]));
            const bindGroup = this.device.createBindGroup({ layout: this._materialLayout, entries: [
                { binding: 0, resource: { buffer: colorBuffer, size: 16 } },
                { binding: 1, resource: texture?.view ?? this._whiteView },
                { binding: 2, resource: this._sampler },
            ] });
            const handle = this._resources.add('material', { colorBuffer, bindGroup, texture }, this._owner);
            if (texture) texture.references++;
            this._stats.uploadedBytes += 16;
            return handle;
        } catch (error) {
            this._retire(colorBuffer);
            throw error;
        }
    }

    destroyResource(handle) {
        this._stats.controlCalls++;
        this._requireOwner();
        // Verify a referenced texture before removing its handle from the table.
        const slot = Number.isInteger(handle) ? handle & 0xffff : 0;
        const generation = Number.isInteger(handle) ? Math.floor(handle / 0x10000) : 0;
        const entry = this._resources.slots[slot];
        if (!entry || entry.generation !== generation || entry.owner !== this._owner)
            throw new Error('Invalid or obsolete resource handle.');
        if (entry.kind === 'texture' && entry.value.references)
            throw new Error('A texture referenced by a material cannot be destroyed.');
        this._resources.remove(handle, this._owner);
        this._destroyEntry(entry, false);
    }

    _destroyEntry(entry, immediate) {
        if (entry.kind === 'material') {
            if (entry.value.texture) entry.value.texture.references--;
            if (immediate) entry.value.colorBuffer.destroy();
            else this._retire(entry.value.colorBuffer);
        } else if (entry.kind === 'texture') {
            if (immediate) entry.value.texture.destroy();
            else this._retire(entry.value.texture);
        } else {
            if (immediate) {
                entry.value.vertexBuffer.destroy();
                entry.value.indexBuffer.destroy();
            } else {
                this._retire(entry.value.vertexBuffer);
                this._retire(entry.value.indexBuffer);
            }
        }
    }

    _growPacketStorage(required) {
        if (required <= this._packetBytes.length) return;
        let capacity = this._packetBytes.length;
        while (capacity < required) capacity *= 2;
        this._packetBytes = new Uint8Array(capacity);
        this._packetView = new DataView(this._packetBytes.buffer);
        this._stats.arenaGrowth++;
    }

    _growUniformStorage(required) {
        if (required <= this._uniformCapacity) return;
        let capacity = this._uniformCapacity;
        while (capacity < required) capacity *= 2;
        capacity = Math.min(capacity, maximumDraws);
        const bytes = new Uint8Array(capacity * this._uniformStride);
        const buffer = this._newUniformBuffer(capacity);
        let bindGroup;
        try { bindGroup = this._newViewBindGroup(buffer); }
        catch (error) { buffer.destroy(); throw error; }
        this._retire(this.uniformBuffer);
        this._uniformBytes = bytes;
        this._uniformView = new DataView(bytes.buffer);
        this.uniformBuffer = buffer;
        this.bindGroup = bindGroup;
        this._uniformCapacity = capacity;
        this._stats.arenaGrowth++;
    }

    submitPacket(memoryView) {
        this._stats.controlCalls++;
        this._requireOwner();
        try {
            const length = byteLengthOf(memoryView);
            if (length < packetHeaderBytes || length > packetHeaderBytes + maximumDraws * drawRecordBytes)
                throw new RangeError('Invalid frame packet length.');
            this._growPacketStorage(length);
            memoryView.copyTo(this._packetBytes);
            this._stats.copiedBytes += length;
            const data = this._packetView;
            const count = validateFramePacket(data, length, this._owner, this._generation,
                this._arenaGeneration, this._frameSequence, this._width, this._height, this._resources);
            if (!this._configured) throw new Error('The canvas surface is suspended.');
            this._executing = true;
            const clear = this._colorAttachment.clearValue;
            clear.r = data.getFloat32(64, true);
            clear.g = data.getFloat32(68, true);
            clear.b = data.getFloat32(72, true);
            clear.a = data.getFloat32(76, true);
            this._growUniformStorage(count);
            for (let i = 0, base = packetHeaderBytes; i < count; i++, base += drawRecordBytes) {
                const offset = i * this._uniformStride;
                for (let component = 0; component < 16; component++)
                    this._uniformView.setUint32(offset + component * 4, data.getUint32(base + 48 + component * 4, true), true);
            }
            if (count) {
                const uploadBytes = count * this._uniformStride;
                this.device.queue.writeBuffer(this.uniformBuffer, 0, this._uniformBytes, 0, uploadBytes);
                this._stats.uploadedBytes += uploadBytes;
            }
            // Current swapchain view, encoder, pass, and command buffer necessarily belong to this frame.
            this._colorAttachment.view = this.context.getCurrentTexture().createView();
            this._depthAttachment.view = this.depthView;
            const encoder = this.device.createCommandEncoder();
            const pass = encoder.beginRenderPass(this._renderPassDescriptor);
            pass.setPipeline(this.pipeline);
            for (let i = 0, base = packetHeaderBytes; i < count; i++, base += drawRecordBytes) {
                const mesh = this._resources.get(data.getUint32(base + 8, true), data.getUint32(base + 12, true), 'mesh', this._owner);
                const material = this._resources.get(data.getUint32(base + 16, true), data.getUint32(base + 20, true), 'material', this._owner);
                this._dynamicOffsets[0] = i * this._uniformStride;
                pass.setBindGroup(0, this.bindGroup, this._dynamicOffsets);
                pass.setBindGroup(1, material.bindGroup);
                const x = data.getUint32(base + 24, true), y = data.getUint32(base + 28, true);
                const width = data.getUint32(base + 32, true), height = data.getUint32(base + 36, true);
                pass.setViewport(x, y, width, height, 0, 1);
                pass.setScissorRect(x, y, width, height);
                pass.setVertexBuffer(0, mesh.vertexBuffer);
                pass.setIndexBuffer(mesh.indexBuffer, 'uint32');
                pass.drawIndexed(data.getUint32(base + 44, true), 1, data.getUint32(base + 40, true));
            }
            pass.end();
            this._submission[0] = encoder.finish();
            this.device.queue.submit(this._submission);
            this._arenaGeneration = data.getUint32(36, true);
            this._frameSequence = data.getUint32(40, true);
            this._stats.packets++;
            this._stats.draws += count;
        } catch (error) {
            this._stats.rejectedPackets++;
            throw error;
        } finally {
            this._executing = false;
            this._submission[0] = null;
            this._colorAttachment.view = undefined;
            this._depthAttachment.view = undefined;
        }
    }

    getStatistics() {
        return { ...this._stats, shaderArtifact: this._shaderArtifact && {
            identity: this._shaderArtifact.identity,
            requiredFeatures: [...this._shaderArtifact.requiredFeatures],
            requiredLimits: { ...this._shaderArtifact.requiredLimits },
        } };
    }

    dispose() {
        if (this._disposed) return;
        this._disposed = true;
        if (this._configured) {
            try { this.context.unconfigure(); } catch (error) { console.error(error); }
        }
        this._configured = false;
        this._resources.clear(entry => this._destroyEntry(entry, true));
        for (const resource of this._retired) resource.destroy();
        this._retired.clear();
        try { this.depthTexture?.destroy(); } catch (error) { console.error(error); }
        try { this._whiteTexture?.destroy(); } catch (error) { console.error(error); }
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
