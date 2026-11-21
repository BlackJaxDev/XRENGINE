namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// The backing object of an authored post-process stage. It has no typed properties, so it stores each parameter
/// value by name. <see cref="PostProcessStageState"/> writes every value change to the bag, and shaders and commands
/// read values by parameter name.
/// </summary>
public sealed class PostProcessPropertyBag
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Occurs after a value changes. The argument is the parameter name.</summary>
    public event Action<string>? ValueChanged;

    /// <summary>Gets the stored values.</summary>
    public IReadOnlyDictionary<string, object?> Values => _values;

    /// <summary>Stores a value.</summary>
    public void Set(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_values.TryGetValue(name, out object? previous) && Equals(previous, value))
            return;

        _values[name] = value;
        ValueChanged?.Invoke(name);
    }

    /// <summary>Returns a value, or <paramref name="fallback"/> when the bag has no value of type <typeparamref name="T"/>.</summary>
    public T Get<T>(string name, T fallback)
        => _values.TryGetValue(name, out object? raw) && raw is T typed ? typed : fallback;

    /// <summary>Returns whether the bag has a value with the name.</summary>
    public bool Contains(string name)
        => _values.ContainsKey(name);
}
