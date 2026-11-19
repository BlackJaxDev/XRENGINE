namespace XREngine.UnityScriptHost.Admission;

/// <summary>Records one source path and its content fingerprint without an absolute machine path.</summary>
public sealed record UnitySourceEntry(string Path, string Kind, long Length, string Sha256);
