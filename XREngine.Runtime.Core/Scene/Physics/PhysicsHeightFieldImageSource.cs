namespace XREngine.Scene.Physics;

/// <summary>Composition point for a height-map image decoder.</summary>
public static class PhysicsHeightFieldImageSource
{
    private static IPhysicsHeightFieldImageSource? s_current;

    public static void Install(IPhysicsHeightFieldImageSource source)
        => Volatile.Write(ref s_current, source ?? throw new ArgumentNullException(nameof(source)));

    public static PhysicsHeightFieldImage Load(string imagePath)
        => Volatile.Read(ref s_current)?.Load(imagePath) ?? throw new InvalidOperationException(
            "No height-field image decoder is installed. Register an imaging module before loading height fields from files.");
}
