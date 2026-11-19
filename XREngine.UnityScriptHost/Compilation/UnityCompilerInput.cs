namespace XREngine.UnityScriptHost.Compilation;

/// <summary>Records the exact source, compiler, or reference bytes supplied to compilation.</summary>
public sealed record UnityCompilerInput(string Kind, string Identity, string Sha256, long Length);
