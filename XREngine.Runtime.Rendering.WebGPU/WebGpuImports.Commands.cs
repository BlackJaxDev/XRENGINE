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

    [JSImport("prepareCommands", "xrengine.webgpu")]
    internal static partial int PrepareCommands(int session, string descriptorJson);

    [JSImport("submitPreparedCommands", "xrengine.webgpu")]
    internal static partial bool SubmitPreparedCommands(int session, int commandsHandle);
}
