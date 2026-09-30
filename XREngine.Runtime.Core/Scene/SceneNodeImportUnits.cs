using System.Runtime.CompilerServices;

namespace XREngine.Scene;

/// <summary>
/// Retains the source model's units-per-meter scale that an importer observed for an imported
/// scene root. The value is ephemeral import evidence: it is not serialized and is absent for
/// scenes loaded from assets or constructed in code.
/// </summary>
public static class SceneNodeImportUnits
{
    private static readonly ConditionalWeakTable<SceneNode, StrongBox<float>> UnitsPerMeter = new();

    /// <summary>Gets the recorded source units per meter, or null when no importer recorded one.</summary>
    public static float? GetImportedModelUnitsPerMeter(this SceneNode sceneRoot)
    {
        ArgumentNullException.ThrowIfNull(sceneRoot);
        return UnitsPerMeter.TryGetValue(sceneRoot, out StrongBox<float>? units) ? units.Value : null;
    }

    /// <summary>Records or clears the source units per meter for an imported scene root.</summary>
    public static void SetImportedModelUnitsPerMeter(this SceneNode sceneRoot, float? unitsPerMeter)
    {
        ArgumentNullException.ThrowIfNull(sceneRoot);
        UnitsPerMeter.Remove(sceneRoot);
        if (unitsPerMeter is float value)
            UnitsPerMeter.Add(sceneRoot, new StrongBox<float>(value));
    }
}
