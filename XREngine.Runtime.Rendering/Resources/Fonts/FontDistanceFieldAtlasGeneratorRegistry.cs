namespace XREngine.Rendering;

/// <summary>Optional native distance-field atlas generator installed by a text authoring backend.</summary>
public static class FontDistanceFieldAtlasGeneratorRegistry
{
    private static IFontDistanceFieldAtlasGenerator? _current;

    public static IFontDistanceFieldAtlasGenerator? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
