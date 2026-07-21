namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Describes how a command recording reacts to a dependency change.
/// </summary>
internal enum CommandRecordingInvalidationClass
{
    None,
    DataOnly,
    BindingIdentity,
    Structural,
}
