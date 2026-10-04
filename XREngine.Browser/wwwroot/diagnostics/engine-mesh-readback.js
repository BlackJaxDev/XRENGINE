/** Cold diagnostic snapshots of one submitted engine packet and bounded canonical targets. */
export function captureSubmittedEngineFrame(renderer, owner) {
    const frame = renderer.commands.engineFrame, packet = frame.view;
    const count = packet.getUint32(12, true), sequence = packet.getUint32(40, true);
    if (packet.getUint32(0, true) !== 0x45475258 || packet.getUint32(4, true) !== 5 ||
        packet.getUint32(16, true) !== owner || packet.getUint32(20, true) !== renderer._generation ||
        count < 1 || count > 4097 || sequence !== frame.lastSequence || frame.stats.submittedFrames < 1)
        throw new Error('A current submitted engine packet is required for Unlit execution evidence.');
    const texture = view => ({ label: view.texture?.label ?? (view.canvasHandle === 0 ? 'canvas' : ''),
        format: view.format, sampleCount: view.sampleCount,
        nativeSampleCount: view.texture?.texture?.sampleCount ?? null });
    const operations = [];
    for (let record = 0; record < count; record++) {
        const base = 48 + record * 112;
        const retained = renderer.commands.get(packet.getInt32(base, true), 'commands');
        if (retained.operations.length !== 1) throw new Error('The submitted engine operation is no longer retained.');
        const operation = retained.operations[0];
        const packetFlags = packet.getUint32(base + 76, true);
        const rectangle = offset => ({ x: packet.getUint32(base + offset, true),
            y: packet.getUint32(base + offset + 4, true), width: packet.getUint32(base + offset + 8, true),
            height: packet.getUint32(base + offset + 12, true) });
        const instanceCountOverride = packetFlags & 1 ? packet.getUint32(base + 72, true) : null;
        const viewportOverride = packetFlags & 2 ? rectangle(80) : null;
        const scissorOverride = packetFlags & 4 ? rectangle(96) : null;
        let raster = null;
        if (operation.type === 'render') {
            // These are the accepted packet's overrides, in the same precedence
            // order as GpuCommands.encodeOperation; a retained plan alone is not draw evidence.
            const fullTarget = { x: 0, y: 0, width: operation.plan.signature.width,
                height: operation.plan.signature.height };
            const viewport = { ...(viewportOverride ?? operation.viewport ?? fullTarget) };
            const scissor = { ...(scissorOverride ?? operation.scissor ?? fullTarget) };
            const scissorSuppressesDraw = scissor.width === 0 || scissor.height === 0;
            const rasterAreaEmpty = Math.min(viewport.x + viewport.width, scissor.x + scissor.width) <=
                Math.max(viewport.x, scissor.x) || Math.min(viewport.y + viewport.height, scissor.y + scissor.height) <=
                Math.max(viewport.y, scissor.y);
            raster = { viewport, scissor, scissorSuppressesDraw, rasterAreaEmpty };
        }
        const drawEvidence = operation.draws?.map(draw => {
            const type = draw.type ?? 'drawIndexed', indirect = type === 'drawIndirect' || type === 'drawIndexedIndirect';
            const issued = !raster.scissorSuppressesDraw;
            const geometryCount = indirect ? null : type === 'draw' ? draw.vertexCount : draw.indexCount;
            const instanceCount = indirect ? null : instanceCountOverride !== null && operation.engineInstanceCountLimit
                ? instanceCountOverride : draw.instanceCount;
            // Positive direct inputs are observable here. Indirect argument values
            // remain GPU-owned and unknown; this diagnostic never reads them back.
            const effective = !issued || raster.rasterAreaEmpty ? false : indirect ? null :
                geometryCount > 0 && instanceCount > 0;
            return { type, issued, issuedCalls: issued ? indirect ? draw.drawCount : 1 : 0,
                geometryCount, instanceCount, effective };
        }) ?? [];
        operations.push({ record, type: operation.type, label: operation.label,
            pipeline: operation.pipeline?.label ?? null,
            sampleCount: operation.plan?.signature.sampleCount ?? null,
            packetFlags, instanceCountOverride, viewportOverride, scissorOverride, raster,
            preparedDraws: drawEvidence.map(draw => draw.type),
            issuedDraws: drawEvidence.filter(draw => draw.issued).map(draw => draw.type),
            draws: drawEvidence.filter(draw => draw.effective === true).map(draw => draw.type), drawEvidence,
            attachments: operation.plan?.bindings.map(binding => ({ key: binding.key, ...texture(binding.source) })) ?? [],
            sampledTextures: operation.bindings?.flatMap(group => group.resources
                .filter(resource => resource.kind === 'texture' && resource.role === 'sampled texture')
                .map(resource => texture(resource.value))) ?? [],
        });
    }
    return { sequence, records: count, operations };
}

export function requireEngineDiagnosticTarget(renderer, owner, name) {
    const matches = renderer._resources.slots.filter(entry => entry?.owner === owner &&
        entry.kind === 'texture' && entry.value.label === name && !entry.value.retired);
    if (matches.length !== 1) throw new Error(`Expected one current engine target '${name}'; found ${matches.length}.`);
    const target = matches[0].value;
    if (!['rgba16float', 'depth32float'].includes(target.format) ||
        target.sampleCount !== 1 || target.texture.sampleCount !== 1 ||
        !(target.usage & GPUTextureUsage.COPY_SRC) || target.width < 1 || target.height < 1 ||
        target.width > 1024 || target.height > 1024)
        throw new Error(`Engine target '${name}' is not a bounded canonical diagnostic copy source.`);
    return target;
}

/** Depth copies cover the whole subresource; returned pixels use exactly the color ROI's row-major order. */
export async function readEngineDiagnosticRegion(renderer, owner, name, region) {
    const target = requireEngineDiagnosticTarget(renderer, owner, name);
    const slot = renderer._resources.slots.findIndex(entry => entry?.owner === owner &&
        entry.kind === 'texture' && entry.value === target);
    const generation = renderer._resources.slots[slot].generation;
    const { x, y, width, height } = region;
    if (![x, y, width, height].every(Number.isInteger) || x < 0 || y < 0 || width < 1 || height < 1 ||
        x + width > target.width || y + height > target.height || width * height > 65536)
        throw new Error('The engine diagnostic region exceeds its target or pixel budget.');
    const depth = target.format === 'depth32float', channels = depth ? 1 : 4, bytesPerPixel = depth ? 4 : 8;
    const copyWidth = depth ? target.width : width, copyHeight = depth ? target.height : height;
    const bytesPerRow = Math.ceil(copyWidth * bytesPerPixel / 256) * 256;
    const byteLength = bytesPerRow * copyHeight;
    if (byteLength > 4 * 1024 * 1024) throw new Error('The engine diagnostic byte budget was exceeded.');
    const buffer = renderer.device.createBuffer({ label: `Engine ${name} diagnostic region`, size: byteLength,
        usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
    let deadline;
    try {
        const encoder = renderer.device.createCommandEncoder({ label: `Engine ${name} diagnostic copy` });
        encoder.copyTextureToBuffer({ texture: target.texture, aspect: depth ? 'depth-only' : 'all',
            origin: depth ? [0, 0, 0] : [x, y, 0] },
            { buffer, bytesPerRow, rowsPerImage: copyHeight }, [copyWidth, copyHeight, 1]);
        renderer.device.queue.submit([encoder.finish()]);
        await Promise.race([buffer.mapAsync(GPUMapMode.READ), new Promise((_, reject) => {
            deadline = setTimeout(() => reject(new Error('Engine region readback exceeded 15 seconds.')), 15000);
        })]);
        if (requireEngineDiagnosticTarget(renderer, owner, name) !== target)
            throw new Error('The engine target changed during diagnostic readback.');
        const view = new DataView(buffer.getMappedRange()), pixels = new Array(width * height * channels);
        const decodeHalf = bits => {
            const sign = bits & 0x8000 ? -1 : 1, exponent = (bits >> 10) & 31, fraction = bits & 1023;
            return sign * (exponent === 0 ? fraction * 2 ** -24 : exponent === 31
                ? (fraction ? NaN : Infinity) : (1 + fraction / 1024) * 2 ** (exponent - 15));
        };
        const min = new Array(channels).fill(Infinity), max = new Array(channels).fill(-Infinity), sum = new Array(channels).fill(0);
        for (let row = 0; row < height; row++) for (let column = 0; column < width; column++) {
            const offset = (row + (depth ? y : 0)) * bytesPerRow + (column + (depth ? x : 0)) * bytesPerPixel;
            for (let channel = 0; channel < channels; channel++) {
                const value = depth ? view.getFloat32(offset, true) : decodeHalf(view.getUint16(offset + channel * 2, true));
                if (!Number.isFinite(value)) throw new Error(`The engine '${name}' target contains a non-finite sample.`);
                pixels[(row * width + column) * channels + channel] = value;
                min[channel] = Math.min(min[channel], value); max[channel] = Math.max(max[channel], value); sum[channel] += value;
            }
        }
        return { label: target.label, format: target.format, width: target.width, height: target.height,
            slot, generation, region: { x, y, width, height },
            sampleCount: target.sampleCount, nativeSampleCount: target.texture.sampleCount, channels, pixels,
            min, max, average: sum.map(value => value / (width * height)) };
    } finally {
        clearTimeout(deadline);
        if (buffer.mapState === 'mapped') buffer.unmap();
        buffer.destroy();
    }
}
