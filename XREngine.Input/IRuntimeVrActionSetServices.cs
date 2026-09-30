namespace XREngine.Input;

/// <summary>Reports changes to the active runtime's action set without exposing native action objects.</summary>
public interface IRuntimeVrActionSetServices
{
    event Action? ActionsChanged;
    bool HasActions { get; }
}
