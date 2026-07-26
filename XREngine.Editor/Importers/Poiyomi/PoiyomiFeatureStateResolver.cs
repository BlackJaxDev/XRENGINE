namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Resolves feature activation without letting dormant texture assignments override explicit section toggles.
/// </summary>
internal static class PoiyomiFeatureStateResolver
{
    public static bool IsEnabled(
        UnityMaterialDocument document,
        bool authoredEvidence,
        string[] toggleProperties,
        string[] keywords)
    {
        bool hasAuthoredToggle = false;
        bool hasEnabledToggle = false;
        foreach (string toggle in toggleProperties)
        {
            if (document.TryGetFloat(toggle, out float floatValue))
            {
                hasAuthoredToggle = true;
                hasEnabledToggle |= floatValue > 0.5f;
            }
            else if (document.TryGetInt(toggle, out int intValue))
            {
                hasAuthoredToggle = true;
                hasEnabledToggle |= intValue != 0;
            }
        }

        if (hasAuthoredToggle)
            return hasEnabledToggle;

        foreach (string keyword in keywords)
        {
            if (document.ValidKeywords.Contains(keyword))
                return true;
        }

        return authoredEvidence;
    }
}
