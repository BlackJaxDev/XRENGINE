namespace XREngine.UnityScriptHost.Analysis;

/// <summary>Reports bounded managed DLL metadata without approving or executing the DLL.</summary>
public sealed record UnityManagedAssemblyInfo
{
    public string SourcePath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long Length { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Culture { get; init; } = string.Empty;
    public string PublicKeyToken { get; init; } = string.Empty;
    public bool IsIlOnly { get; init; }
    public bool Requires32Bit { get; init; }
    public UnityManagedAssemblyReference[] References { get; init; } = [];
    public string[] ModuleReferences { get; init; } = [];
    public int PInvokeMethodCount { get; init; }
    public int InternalCallMethodCount { get; init; }
    public int NativeMethodBodyCount { get; init; }
    public int TypeDefinitionCount { get; init; }
}
