namespace XREngine.UnityScriptHost.Admission;

/// <summary>Records a source assembly-reference file for later assembly graph resolution.</summary>
public sealed record UnityAssemblyReference(string Path, string Reference);
