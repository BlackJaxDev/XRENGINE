namespace XREngine.Rendering.Commands;

/// <summary>
/// Describes the precise table mutation represented by an
/// <see cref="AdvancedGpuRecordPublicationDelta"/>.
/// </summary>
public enum EAdvancedGpuRecordPublicationChange : byte
{
    Added,
    Updated,
    Tombstoned,
    DenseRemapped,
}
