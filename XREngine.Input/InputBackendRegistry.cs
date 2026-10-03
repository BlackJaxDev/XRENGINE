namespace XREngine.Input.Devices;

/// <summary>Explicitly installed factories for platform-specific input devices.</summary>
public static class InputBackendRegistry
{
    private static readonly Dictionary<EInputType, Func<int, BaseGamePad>> GamepadFactories = [];

    /// <summary>Installs a gamepad backend during application composition.</summary>
    public static void RegisterGamepad(EInputType type, Func<int, BaseGamePad> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (GamepadFactories)
            GamepadFactories[type] = factory;
    }

    /// <summary>Creates a gamepad from a statically installed backend.</summary>
    public static BaseGamePad CreateGamepad(EInputType type, int index)
    {
        Func<int, BaseGamePad> factory;
        lock (GamepadFactories)
            if (!GamepadFactories.TryGetValue(type, out factory!))
                throw new NotSupportedException($"The '{type}' gamepad backend is not installed in this application.");
        return factory(index);
    }
}
