namespace XREngine.UnityScriptHost.Compilation;

/// <summary>Reports an assembly selection error or a graph limitation.</summary>
public sealed record UnityAssemblyGraphDiagnostic(string Code, string Assembly, string Path, string Message, bool IsError);
