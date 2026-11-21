namespace XREngine.UnityScriptHost.Admission;

/// <summary>Records a version-dependent symbol rule without evaluating package version expressions.</summary>
public sealed record UnityAssemblyVersionDefine(string Name, string Expression, string Define);
