namespace XREngine.Execution;

/// <summary>
/// Identifies the configuration layer that supplied an execution-topology value.
/// </summary>
public enum EEngineExecutionSettingSource : byte
{
    BuiltInDefault = 0,
    EngineDefault = 1,
    Project = 2,
    User = 3,
    Environment = 4,
}
