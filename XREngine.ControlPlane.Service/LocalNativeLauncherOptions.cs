namespace XREngine.ControlPlane.Service;

/// <summary>Explicit local executable allowlist used by the sample management UI.</summary>
public sealed class LocalNativeLauncherOptions
{
    /// <summary>Enables only same-machine native process starts from the local service.</summary>
    public bool Enabled { get; set; }

    /// <summary>Absolute or service-config-relative game executable path.</summary>
    public string GameExecutable { get; set; } = string.Empty;

    /// <summary>Operator-supplied fixed arguments. Browser requests cannot add arguments.</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>
    /// Validates the local native launcher options, ensuring that the game executable is correctly configured and exists.
    /// </summary>
    /// <param name="configurationDirectory">The directory relative to which the game executable path should be resolved.</param>
    /// <exception cref="InvalidOperationException">Thrown if the game executable is not configured correctly or does not exist.</exception>
    internal void Validate(string configurationDirectory)
    {
        if (!Enabled)
            return;
        
        if (string.IsNullOrWhiteSpace(GameExecutable))
            throw new InvalidOperationException("Native launcher is enabled but GameExecutable is not configured.");

        GameExecutable = Path.GetFullPath(GameExecutable, configurationDirectory);

        if (!File.Exists(GameExecutable) || !GameExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native launcher GameExecutable must name an existing .exe file.");
    }
}
