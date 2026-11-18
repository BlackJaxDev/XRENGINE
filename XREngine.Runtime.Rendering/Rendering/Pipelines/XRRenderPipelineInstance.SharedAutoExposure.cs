namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    private SharedAutoExposureChannel? _publishesAutoExposureTo;
    private SharedAutoExposureChannel? _consumesAutoExposureFrom;

    /// <summary>
    /// When set, the auto exposure update copies this instance's exposure value into the
    /// channel after it meters the scene. Other instances that render later in the same
    /// frame read the value from the channel.
    /// </summary>
    public SharedAutoExposureChannel? PublishesAutoExposureTo
    {
        get => Volatile.Read(ref _publishesAutoExposureTo);
        set => Volatile.Write(ref _publishesAutoExposureTo, value);
    }

    /// <summary>
    /// When set, the auto exposure update does not meter this instance's own scene image.
    /// It copies the exposure that the channel's publisher wrote in the same render frame.
    /// </summary>
    public SharedAutoExposureChannel? ConsumesAutoExposureFrom
    {
        get => Volatile.Read(ref _consumesAutoExposureFrom);
        set => Volatile.Write(ref _consumesAutoExposureFrom, value);
    }
}
