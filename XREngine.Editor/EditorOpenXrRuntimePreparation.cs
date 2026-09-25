namespace XREngine.Editor;

/// <summary>Validated runtime resources prepared before the editor changes OpenXR state.</summary>
internal sealed record EditorOpenXrRuntimePreparation(
    string RuntimeJsonPath,
    Func<string, bool>? RuntimeServiceEnsurer,
    bool RecommendedDimensionsRequireServiceRestart);
