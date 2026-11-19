namespace UnityEngine;

/// <summary>Declares Unity serialization callbacks for an ordered worker lifecycle.</summary>
public interface ISerializationCallbackReceiver
{
    /// <summary>Runs before serialization when the worker schedules this callback.</summary>
    void OnBeforeSerialize();

    /// <summary>Runs after deserialization when the worker schedules this callback.</summary>
    void OnAfterDeserialize();
}
