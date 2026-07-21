namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Result of comparing two immutable command-recording dependency snapshots.
/// </summary>
internal readonly record struct CommandRecordingDependencyMismatch(
    CommandRecordingDependencyField Field,
    CommandRecordingInvalidationClass InvalidationClass)
{
    public static CommandRecordingDependencyMismatch None { get; } = new(
        CommandRecordingDependencyField.None,
        CommandRecordingInvalidationClass.None);

    public bool RequiresRecording =>
        InvalidationClass is CommandRecordingInvalidationClass.Structural or
            CommandRecordingInvalidationClass.BindingIdentity;
}
