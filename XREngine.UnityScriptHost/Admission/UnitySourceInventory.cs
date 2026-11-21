namespace XREngine.UnityScriptHost.Admission;

/// <summary>Describes inspected source bytes. This report is not a snapshot, trust grant, or executable build.</summary>
public sealed record UnitySourceInventory
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string SourceContentSha256 { get; init; } = string.Empty;
    public string EntryPath { get; init; } = string.Empty;
    public string RootName { get; init; } = string.Empty;
    public string UnityEditorVersion { get; init; } = string.Empty;
    public bool ScriptExecutionAvailable => false;
    public bool RootSelectionValidated => false;
    public bool AssemblyGraphResolved => false;
    public long TotalFileBytes { get; init; }
    public int SuppressedDiagnosticCount { get; init; }
    public UnitySourceEntry[] Entries { get; init; } = [];
    public UnitySourcePackage[] Packages { get; init; } = [];
    public UnityAssemblyDefinition[] AssemblyDefinitions { get; init; } = [];
    public UnityAssemblyReference[] AssemblyReferences { get; init; } = [];
    public UnityInventoryDiagnostic[] Diagnostics { get; init; } = [];
}
