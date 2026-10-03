namespace XREngine.Input;

public sealed class RuntimeOpenVrApplicationManifest
{
    public string? AppKey { get; set; }
    public string? WindowsPath { get; set; }
    public string? WindowsArguments { get; set; }
    public string? OSXPath { get; set; }
    public string? OSXArguments { get; set; }
    public string? LinuxPath { get; set; }
    public string? LinuxArguments { get; set; }
    public string? Icon { get; set; }
    public bool IsDashboardOverlay { get; set; }
    public string? ActionManifestPath { get; set; }
    public Dictionary<string, RuntimeOpenVrApplicationNameDescription>? LocalizedNames { get; set; }
}
