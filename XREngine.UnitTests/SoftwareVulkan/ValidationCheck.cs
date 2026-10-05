namespace XREngine.UnitTests.SoftwareVulkan;

/// <summary>A correctness check outcome; skipped checks are never reported as passed.</summary>
internal sealed record ValidationCheck(string Name, string Status, string Detail);
