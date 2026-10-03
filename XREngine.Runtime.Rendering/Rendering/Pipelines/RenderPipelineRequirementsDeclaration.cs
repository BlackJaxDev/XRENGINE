namespace XREngine.Rendering;

/// <summary>
/// Serializable declarations for authored pipelines, callback commands, and resource factories.
/// References remain part of the normal cooked asset graph; no factory runs during discovery.
/// </summary>
public sealed class RenderPipelineRequirementsDeclaration
{
    [System.ComponentModel.DefaultValue("webgpu")]
    public string Backend { get; set; } = "webgpu";
    public List<string> Operations { get; set; } = [];
    public List<EAntiAliasingMode> SupportedAntiAliasingModes { get; set; } = [];
    public Dictionary<string, string?> Programs { get; set; } = new(StringComparer.Ordinal);
    public List<int> ScenePasses { get; set; } = [];
    public Dictionary<int, XREngine.Data.Rendering.EMeshSubmissionStrategy?> NativeScenePasses { get; set; } = [];
    public bool RequiresNativeScenePasses { get; set; }
    public bool NativeProbeIbl { get; set; }
    public List<XRMaterial> Materials { get; set; } = [];
    public List<string> ProgramIdentities { get; set; } = [];

    // These mutable lists are authored directly, including after hydration. Keep
    // their relevant state observable without allocating or requiring eventful lists.
    internal uint AuthoredDecalConsumerState
        => (Backend == "webgpu" ? 1u : 0u) |
           (Operations.Contains("native-authored-decals") ? 2u : 0u) |
           (ScenePasses.Contains((int)EDefaultRenderPass.DeferredDecals) ? 4u : 0u);

    internal void ApplyTo(RenderPipelineRequirements requirements)
    {
        requirements.ObserveAuthoredDecalDeclaration(this);
        if (!string.Equals(Backend, requirements.Backend.Value, StringComparison.Ordinal))
            return;
        foreach (string operation in Operations) requirements.RequireOperation(operation);
        foreach (EAntiAliasingMode mode in SupportedAntiAliasingModes) requirements.SupportedAntiAliasingModes.Add(mode);
        foreach ((string pass, string? identity) in Programs) requirements.RequireProgram(pass, identity);
        foreach (int pass in ScenePasses) requirements.RequireRasterScenePass(pass);
        foreach ((int pass, XREngine.Data.Rendering.EMeshSubmissionStrategy? strategy) in NativeScenePasses)
            requirements.RequireNativeScenePass(pass, strategy);
        requirements.RequiresNativeScenePasses |= RequiresNativeScenePasses;
        requirements.NativeProbeIbl |= NativeProbeIbl;
        foreach (XRMaterial material in Materials) requirements.RequireMaterial(material);
        foreach (string identity in ProgramIdentities) requirements.RequireProgramIdentity(identity);
    }
}
