using System.Collections.ObjectModel;

namespace XREngine.Rendering;

/// <summary>A validated device snapshot, independent of the backend's static module metadata.</summary>
public sealed class BrowserDeviceCapabilities
{
    public BrowserDeviceCapabilities(string profile, string[] features, Dictionary<string, long> limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(limits);
        Profile = profile;
        Features = Array.AsReadOnly((string[])features.Clone());
        Limits = new ReadOnlyDictionary<string, long>(new Dictionary<string, long>(limits, StringComparer.Ordinal));
    }

    public string Profile { get; }
    public IReadOnlyList<string> Features { get; }
    public IReadOnlyDictionary<string, long> Limits { get; }
}
