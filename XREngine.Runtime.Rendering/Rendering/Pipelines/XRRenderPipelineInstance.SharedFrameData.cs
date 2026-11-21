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

    private XRRenderPipelineInstance? _consumesVisibilityFrom;

    /// <summary>
    /// When set, this instance's Advanced early visibility draws only the objects that the
    /// other instance drew in the same frame, intersected with this instance's frustum, and
    /// defers nothing to late visibility. The other instance must render the same eyes from
    /// the same positions with wider frustums, and must record earlier in the frame. The
    /// OpenXR quad view links the inset pair to the wide pair this way.
    /// </summary>
    public XRRenderPipelineInstance? ConsumesVisibilityFrom
    {
        get => Volatile.Read(ref _consumesVisibilityFrom);
        set => Volatile.Write(ref _consumesVisibilityFrom, value);
    }
}
