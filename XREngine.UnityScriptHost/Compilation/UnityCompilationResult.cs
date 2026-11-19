using XREngine.UnityScriptHost.Contracts;
using XREngine.UnityScriptHost.Analysis;

namespace XREngine.UnityScriptHost.Compilation;

/// <summary>Reports source compilation only. Generated assemblies have not been loaded or executed.</summary>
public sealed record UnityCompilationResult
{
    public int SchemaVersion { get; init; } = 1;
    public bool Succeeded { get; init; }
    public bool DiagnosticsOnly { get; init; }
    public bool ScriptsExecuted => false;
    public string SourceContentSha256 { get; init; } = string.Empty;
    public string CompilationInputSha256 { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public string CompilerRuntimeVersion { get; init; } = string.Empty;
    public UnityCompilationArtifact[] Artifacts { get; init; } = [];
    public UnityCompilerInput[] Inputs { get; init; } = [];
    public UnityCompilerDiagnostic[] Diagnostics { get; init; } = [];
    /// <summary>Gets metadata for captured imported DLLs. This does not grant execution.</summary>
    public UnityManagedAssemblyInfo[] ImportedAssemblies { get; init; } = [];
    /// <summary>Gets compilation blockers without claiming that native output was assessed.</summary>
    public UnityConversionCoverageReport ConversionCoverage { get; init; } = new();
}
