namespace XREngine.Components;

/// <summary>Supplies optional lip-sync sessions from an explicitly installed native backend.</summary>
public static class LipSyncSessionRegistry
{
    private static Func<int, int, ILipSyncSession>? _factory;

    public static void Register(Func<int, int, ILipSyncSession> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public static ILipSyncSession Create(int sampleRate, int bufferSize)
        => (_factory ?? throw new InvalidOperationException("No lip-sync backend is registered."))(sampleRate, bufferSize);
}
