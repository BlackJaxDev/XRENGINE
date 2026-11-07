namespace XREngine.Rendering;

/// <summary>
/// The pipeline settings that data branch conditions can read, keyed by name. A pipeline registers each setting once
/// with a getter. A condition resolves the key to the getter when it binds, so a read invokes one cached delegate and
/// does not allocate or use reflection.
/// </summary>
public sealed class RenderPipelineBranchSettings
{
    private readonly Dictionary<string, Func<double>> _getters = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _keys = [];

    /// <summary>Gets the registered keys in registration order, for editor pickers.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>Registers a bool setting. True reads as 1 and false as 0.</summary>
    public RenderPipelineBranchSettings Add(string key, Func<bool> getter)
    {
        ArgumentNullException.ThrowIfNull(getter);
        return AddCore(key, () => getter() ? 1.0 : 0.0);
    }

    /// <summary>Registers an integer setting, such as an enum cast to int.</summary>
    public RenderPipelineBranchSettings Add(string key, Func<int> getter)
    {
        ArgumentNullException.ThrowIfNull(getter);
        return AddCore(key, () => getter());
    }

    /// <summary>Registers a float setting.</summary>
    public RenderPipelineBranchSettings Add(string key, Func<float> getter)
    {
        ArgumentNullException.ThrowIfNull(getter);
        return AddCore(key, () => getter());
    }

    /// <summary>Registers a double setting.</summary>
    public RenderPipelineBranchSettings Add(string key, Func<double> getter)
    {
        ArgumentNullException.ThrowIfNull(getter);
        return AddCore(key, getter);
    }

    /// <summary>Finds the getter of a setting.</summary>
    public bool TryGet(string key, out Func<double>? getter)
        => _getters.TryGetValue(key, out getter);

    /// <summary>Returns whether a setting with the key exists.</summary>
    public bool Contains(string key)
        => _getters.ContainsKey(key);

    private RenderPipelineBranchSettings AddCore(string key, Func<double> getter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!_getters.TryAdd(key, getter))
            throw new ArgumentException(string.Concat("The branch setting '", key, "' is already registered."), nameof(key));

        _keys.Add(key);
        return this;
    }
}
