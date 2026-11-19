namespace XREngine.UnityScriptHost.Compilation;

/// <summary>Identifies a generated assembly without loading its executable code.</summary>
public sealed record UnityCompilationArtifact(string AssemblyName, string Path, string Sha256, long Length);
