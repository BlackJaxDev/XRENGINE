using System.Collections;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable, ordinally sorted instance-extension names published with one
/// native Vulkan instance.
/// </summary>
internal sealed class VulkanInstanceExtensionSet : IReadOnlyList<string>
{
    public static VulkanInstanceExtensionSet Empty { get; } = new([]);

    private readonly string[] _names;

    public VulkanInstanceExtensionSet(IEnumerable<string> names)
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
