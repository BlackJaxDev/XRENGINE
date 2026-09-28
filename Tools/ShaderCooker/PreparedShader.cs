namespace XREngine.Tools.ShaderCooker;

/// <summary>A validated artifact held in memory until the whole package can be published.</summary>
internal sealed record PreparedShader(string Name, byte[] Descriptor, byte[] Source);
