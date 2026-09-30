namespace XREngine.Imaging;

/// <summary>Selected image codec for authoring and diagnostic capture.</summary>
public static class RuntimeImageCodecs
{
    private static IRuntimeImageCodec? _current;

    public static IRuntimeImageCodec? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public static IRuntimeImageCodec Require()
        => Current ?? throw new InvalidOperationException(
            "No image codec is registered. Register an imaging backend in the host application.");
}
