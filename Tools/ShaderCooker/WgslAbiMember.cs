namespace XREngine.Tools.ShaderCooker;

internal sealed record WgslAbiMember(string Name, string Type, List<WgslAbiAttribute> Attributes, int Offset);
