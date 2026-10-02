/** Register the shared WebGPU browser executor for either a reference or authored engine world. */
export function installWebGpuImports(runtime, renderers) {
    const renderer = id => {
        const value = renderers.get(id);
        if (!value) throw new Error('The frame belongs to an inactive canvas session.');
        return value;
    };
    runtime.setModuleImports('xrengine.webgpu', {
        configureSkinning: (id, mesh, packet) => renderer(id).skinning.configure(mesh, packet),
        updateSkinning: (id, mesh, palette, morphs) => renderer(id).skinning.update(mesh, palette, morphs),
        releaseSkinning: (id, mesh) => {
            const value = renderer(id);
            value._requireOwner();
            value.skinning.releaseMesh(mesh);
        },
        getCapabilities: id => JSON.stringify(renderer(id).getCapabilities()),
        configurePipeline: (id, json) => renderer(id).focusedPipeline.configure(json),
        configurePipelineMaterial: (id, material, json) => renderer(id).focusedPipeline.configureMaterial(material, json),
        submitPipelinePacket: (id, bytes) => renderer(id).focusedPipeline.submit(bytes),
        createBuffer: (id, size, usage, label) => renderer(id).resources.createBuffer(size, usage, label),
        writeBuffer: (id, handle, offset, bytes) => renderer(id).resources.writeBuffer(handle, offset, bytes),
        copyBuffer: (id, source, sourceOffset, destination, destinationOffset, size) =>
            renderer(id).resources.copyBuffer(source, sourceOffset, destination, destinationOffset, size),
        createTextureResource: (id, width, height, mips, samples, format, usage, label, layers) =>
            renderer(id).resources.createTexture(width, height, mips, samples, format, usage, label, layers),
        uploadTextureMip: (id, handle, mip, x, y, width, height, bytes, layer) =>
            renderer(id).resources.uploadTextureMip(handle, mip, x, y, width, height, bytes, layer),
        copyTextureSubresource: (id, source, destination, sourceMip, destinationMip, destinationLayer, width, height) =>
            renderer(id).resources.copyTextureSubresource(source, destination, sourceMip, destinationMip, destinationLayer, width, height),
        createTextureView: (id, texture, baseMip, mipCount, aspect, label, baseLayer, layerCount, dimension) =>
            renderer(id).resources.createTextureView(texture, baseMip, mipCount, aspect, label, baseLayer, layerCount, dimension),
        createSampler: (id, addressU, addressV, addressW, minFilter, magFilter, mipmapFilter, label, lodMaxClamp, maxAnisotropy, lodMinClamp, compare) =>
            renderer(id).resources.createSampler(addressU, addressV, minFilter, magFilter, mipmapFilter, label, lodMaxClamp, maxAnisotropy, lodMinClamp, compare, addressW),
        createMesh: (id, vertices, indices) => renderer(id).createMesh(vertices, indices),
        createTexture: (id, width, height, bytes) => renderer(id).createTexture(width, height, bytes),
        createCookedTexture: (id, description, bytes) => renderer(id).createCookedTexture(description, bytes),
        createMaterial: (id, texture, r, g, b, a) => renderer(id).createMaterial(texture, r, g, b, a),
        destroyResource: (id, handle) => renderer(id).destroyResource(handle),
        submitPacket: (id, packet) => renderer(id).submitPacket(packet),
        submitUploads: (id, commands, payload) => renderer(id).submitUploads(commands, payload),
        copyTexture: (id, source, destination, sourceX, sourceY, destinationX, destinationY, width, height) =>
            renderer(id).copyTexture(source, destination, sourceX, sourceY, destinationX, destinationY, width, height),
        beginBufferReadback: (id, handle, offset, size) => renderer(id).readback.beginBuffer(handle, offset, size),
        beginTextureReadback: (id, handle, mip, x, y, width, height) => renderer(id).readback.beginTexture(handle, mip, x, y, width, height),
        beginCanvasReadback: (id, generation, x, y, width, height) => renderer(id).readback.beginCanvas(generation, x, y, width, height),
        prepareLuminance: (id, source) => renderer(id).luminance.prepare(source),
        beginTextureLuminance: (id, handle, mip, width, height, layers, red, green, blue) =>
            renderer(id).luminance.beginTexture(handle, mip, width, height, layers, red, green, blue),
        beginCanvasLuminance: (id, generation, x, y, width, height, red, green, blue) =>
            renderer(id).luminance.beginCanvas(generation, x, y, width, height, red, green, blue),
        beginCompletion: id => renderer(id).readback.beginCompletion(),
        waitReadback: (id, ticket) => renderer(id).readback.wait(ticket),
        copyReadback: (id, ticket, destination) => renderer(id).readback.copy(ticket, destination),
        releaseReadback: (id, ticket) => renderers.get(id)?.readback.release(ticket),
        createShaderModule: (id, source, label) => renderer(id).commands.createShaderModule(source, label),
        createBindingLayout: (id, json) => renderer(id).commands.createBindingLayout(json),
        createBindingGroup: (id, json) => renderer(id).commands.createBindingGroup(json),
        createRenderPipeline: (id, json) => renderer(id).commands.createRenderPipeline(json),
        createComputePipeline: (id, json) => renderer(id).commands.createComputePipeline(json),
        prepareCommands: (id, json) => renderer(id).commands.prepareCommands(json),
        submitPreparedCommands: (id, handle) => renderer(id).commands.submitPreparedCommands(handle),
        submitEngineFrame: (id, commands, uniforms, storage) => renderer(id).commands.submitEngineFrame(commands, uniforms, storage),
        retireResource: (id, handle) => renderer(id).retireResource(handle),
        disposeRenderer: id => renderers.get(id)?.dispose()
    });
}
