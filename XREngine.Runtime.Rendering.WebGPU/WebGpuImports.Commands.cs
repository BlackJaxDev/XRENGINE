using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Rendering.WebGPU;

internal static partial class WebGpuImports
{
    [JSImport("createShaderModule", "xrengine.webgpu")]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> CreateShaderModuleAsync(int session, string wgsl, string debugName);

    [JSImport("createBindingLayout", "xrengine.webgpu")]
    internal static partial int CreateBindingLayout(int session, string descriptorJson);

    [JSImport("createBindingGroup", "xrengine.webgpu")]
    internal static partial int CreateBindingGroup(int session, string descriptorJson);

    [JSImport("createRenderPipeline", "xrengine.webgpu")]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> CreateRenderPipelineAsync(int session, string descriptorJson);

    [JSImport("createComputePipeline", "xrengine.webgpu")]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> CreateComputePipelineAsync(int session, string descriptorJson);

    [JSImport("getResourcePreparationStatus", "xrengine.webgpu")]
    internal static partial string GetResourcePreparationStatus(int session);

    [JSImport("prepareCommands", "xrengine.webgpu")]
    internal static partial int PrepareCommands(int session, string descriptorJson);

    [JSImport("submitPreparedCommands", "xrengine.webgpu")]
    internal static partial bool SubmitPreparedCommands(int session, int commandsHandle);

    [JSImport("submitEngineFrame", "xrengine.webgpu")]
    internal static partial double SubmitEngineFrame(int session,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> commands,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> uniforms,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> storage,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> preparations,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> preparationPayload,
        string resourceDescriptions,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> resourceReceipts);

    [JSImport("pollEngineResourceReceipts", "xrengine.webgpu")]
    internal static partial void PollEngineResourceReceipts(int session,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> resourceReceipts);

    [JSImport("retireResource", "xrengine.webgpu")]
    internal static partial void RetireResource(int session, int handle);
}
