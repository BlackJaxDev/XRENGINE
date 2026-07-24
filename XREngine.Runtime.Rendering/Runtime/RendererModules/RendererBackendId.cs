namespace XREngine.Rendering;

/// <summary>
/// Stable, assembly-independent identity for a renderer backend module.
/// </summary>
public readonly struct RendererBackendId : IEquatable<RendererBackendId>
{
    private readonly string? _value;

    /// <summary>
    /// The built-in OpenGL backend identifier.
    /// </summary>
    public static RendererBackendId OpenGL { get; } = new("opengl");

    /// <summary>
    /// The built-in Vulkan backend identifier.
    /// </summary>
    public static RendererBackendId Vulkan { get; } = new("vulkan");

    /// <summary>
    /// Creates a normalized renderer backend identifier.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is empty.</exception>
    public RendererBackendId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Gets the normalized identifier value.
    /// </summary>
    public string Value
        => _value ?? string.Empty;

    /// <summary>
    /// Converts a runtime graphics API kind to its stable backend identifier.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an unknown graphics API.</exception>
    public static RendererBackendId FromGraphicsApi(RuntimeGraphicsApiKind apiKind)
        => apiKind switch
        {
            RuntimeGraphicsApiKind.OpenGL => OpenGL,
            RuntimeGraphicsApiKind.Vulkan => Vulkan,
            _ => throw new ArgumentOutOfRangeException(
                nameof(apiKind),
                apiKind,
                "A concrete graphics API is required to resolve a renderer backend module."),
        };

    public bool Equals(RendererBackendId other)
        => StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj)
        => obj is RendererBackendId other && Equals(other);

    public override int GetHashCode()
        => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString()
        => Value;

    public static bool operator ==(RendererBackendId left, RendererBackendId right)
        => left.Equals(right);

    public static bool operator !=(RendererBackendId left, RendererBackendId right)
        => !left.Equals(right);
}
