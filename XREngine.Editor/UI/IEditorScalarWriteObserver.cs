namespace XREngine.Editor.UI;

/// <summary>Tracks exact scalar setter entry without interrupting completed-write settlement.</summary>
internal interface IEditorScalarWriteObserver
{
    void ValidateValue(object target, object? current);
    void EnterSetter(object target, object? previous, object? next);

    /// <summary>Updates receipt state only. Implementations must not throw or call application code.</summary>
    void ExitSetter(object target, bool returnedNormally);
}
