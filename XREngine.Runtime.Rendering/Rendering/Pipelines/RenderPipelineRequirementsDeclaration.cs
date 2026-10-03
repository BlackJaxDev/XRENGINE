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
    public List<XRMaterial> Materials { get; set; } = [];
    public List<string> ProgramIdentities { get; set; } = [];

    internal void ApplyTo(RenderPipelineRequirements requirements)
    {
        if (!string.Equals(Backend, requirements.Backend.Value, StringComparison.Ordinal))
            return;
        foreach (string operation in Operations) requirements.RequireOperation(operation);
        foreach (EAntiAliasingMode mode in SupportedAntiAliasingModes) requirements.SupportedAntiAliasingModes.Add(mode);
        foreach ((string pass, string? identity) in Programs) requirements.RequireProgram(pass, identity);
        foreach (int pass in ScenePasses) requirements.ScenePasses.Add(pass);
        foreach (XRMaterial material in Materials) requirements.RequireMaterial(material);
        foreach (string identity in ProgramIdentities) requirements.RequireProgramIdentity(identity);
    }
}
