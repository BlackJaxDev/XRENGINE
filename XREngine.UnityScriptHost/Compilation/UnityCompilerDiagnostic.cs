namespace XREngine.UnityScriptHost.Compilation;

/// <summary>Reports a compiler or reference-admission problem with its source assembly.</summary>
public sealed record UnityCompilerDiagnostic(string Code, string Assembly, string Message, bool IsError);
