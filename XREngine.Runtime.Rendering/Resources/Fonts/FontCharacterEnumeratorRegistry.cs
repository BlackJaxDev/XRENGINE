namespace XREngine.Rendering;

/// <summary>Selected authoring backend for font character discovery.</summary>
public static class FontCharacterEnumeratorRegistry
{
    private static IFontCharacterEnumerator? _current;

    public static IFontCharacterEnumerator? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public static IFontCharacterEnumerator Require()
        => Current ?? throw new InvalidOperationException(
            "No font character enumerator is registered. Register the FreeType font backend in the authoring application.");
}
