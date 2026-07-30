using System.Collections;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable, ordinally sorted device-extension names with allocation-free lookup.
/// </summary>
internal sealed class VulkanDeviceExtensionSet : IReadOnlyList<string>
{
    public static VulkanDeviceExtensionSet Empty { get; } = new([]);

    private readonly string[] _names;

    public VulkanDeviceExtensionSet(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        _names = names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public int Count => _names.Length;
    public string this[int index] => _names[index];

    public bool Contains(string extensionName)
        => Array.BinarySearch(_names, extensionName, StringComparer.Ordinal) >= 0;

    public IEnumerator<string> GetEnumerator()
        => ((IEnumerable<string>)_names).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => _names.GetEnumerator();
}
