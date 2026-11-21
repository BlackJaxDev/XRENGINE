namespace XREngine.UnityScriptHost.Compilation;

/// <summary>Lists source files and requirements for one selected source assembly.</summary>
public sealed record UnityAssemblyNode
{
    public string Name { get; init; } = string.Empty;
    public string DefinitionPath { get; init; } = string.Empty;
    public string[] SourcePaths { get; init; } = [];
    public string[] References { get; init; } = [];
    public string[] ExternalReferences { get; init; } = [];
    public string[] Defines { get; init; } = [];
    public bool AllowUnsafeCode { get; init; }
    public bool IsEditorOnly { get; init; }
    public bool IsTestAssembly { get; init; }
    public bool NoEngineReferences { get; init; }
}
