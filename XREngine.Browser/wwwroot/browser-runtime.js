import { dotnet } from './_framework/dotnet.js';
import { BrowserCanvasHost } from './browser-canvas-host.js';

/** One runtime may create several independently owned canvas hosts. */
export async function createBrowserRuntime() {
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const renderers = new Map();
    const renderer = id => {
        const value = renderers.get(id);
        if (!value) throw new Error('The frame belongs to an inactive canvas session.');
        return value;
    };
    runtime.setModuleImports('xrengine.audio', {
        updateListener: (id, x, y, z, fx, fy, fz, ux, uy, uz) =>
            renderers.get(id)?.audioService?.setListenerValues(x, y, z, fx, fy, fz, ux, uy, uz)
    });
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
        createTextureResource: (id, width, height, mips, samples, format, usage, label) =>
            renderer(id).resources.createTexture(width, height, mips, samples, format, usage, label),
        uploadTextureMip: (id, handle, mip, x, y, width, height, bytes) =>
            renderer(id).resources.uploadTextureMip(handle, mip, x, y, width, height, bytes),
        createTextureView: (id, texture, baseMip, mipCount, aspect, label) =>
            renderer(id).resources.createTextureView(texture, baseMip, mipCount, aspect, label),
        createSampler: (id, addressU, addressV, minFilter, magFilter, mipmapFilter, label, lodMaxClamp, maxAnisotropy) =>
            renderer(id).resources.createSampler(addressU, addressV, minFilter, magFilter, mipmapFilter, label, lodMaxClamp, maxAnisotropy),
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
        disposeRenderer: id => renderers.get(id)?.dispose()
    });
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
    return (canvas, onState, shaderName) => new BrowserCanvasHost(
        exports.XREngine.Browser.BrowserSceneExports, renderers, canvas, onState, shaderName);
}

