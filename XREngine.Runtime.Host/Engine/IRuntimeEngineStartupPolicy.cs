using XREngine.Data.Vectors;

namespace XREngine;

/// <summary>Supplies application-specific startup defaults and verified launch preparation.</summary>
public interface IRuntimeEngineStartupPolicy
{
    /// <summary>Creates default settings when the caller supplies no settings factory.</summary>
    GameStartupSettings CreateDefaultGameSettings();

    /// <summary>Returns the display extent for a borderless window requested by this host.</summary>
    IVector2 GetPrimaryDisplaySize();

    /// <summary>Applies host settings after game and user settings are assigned, before resource creation.</summary>
    void PrepareSettings(GameStartupSettings settings);

    /// <summary>Prepares verified launch data before the shared networking admission checks.</summary>
    void PrepareNetworking(GameStartupSettings settings);
}
