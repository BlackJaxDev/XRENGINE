namespace XREngine.Tools.ShaderCooker;

/// <summary>A validated artifact held in memory until the whole package can be published.</summary>
internal sealed record PreparedShader(string Name, byte[] Descriptor, byte[] Source,
    System.Text.Json.Nodes.JsonObject? MaterialVariant, System.Text.Json.Nodes.JsonObject? PipelineArtifact,
    System.Text.Json.Nodes.JsonObject? ComputeArtifact, PreparedShader? Companion = null);
