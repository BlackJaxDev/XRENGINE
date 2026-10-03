namespace XREngine.Rendering;

/// <summary>Cold resource preparation and reusable ordered GPU command submission for a bounded browser profile.</summary>
/// <remarks>JSON descriptors use the documented browser GPU command schema. Parsing occurs only during creation.</remarks>
public interface IBrowserCommandCapability
{
    Task<int> CreateShaderModuleAsync(string wgsl, string debugName = "");
    int CreateBindingLayout(string descriptorJson);
    int CreateBindingGroup(string descriptorJson);
    Task<int> CreateRenderPipelineAsync(string descriptorJson);
    Task<int> CreateComputePipelineAsync(string descriptorJson);
    int PrepareCommands(string descriptorJson);
    void SubmitPreparedCommands(int commandsHandle);
}
