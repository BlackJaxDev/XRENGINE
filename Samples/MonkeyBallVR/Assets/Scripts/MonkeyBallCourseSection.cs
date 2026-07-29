namespace MonkeyBallVR;

/// <summary>
/// Asset-authored rectangular course span used by the lightweight arcade collision model.
/// </summary>
public sealed class MonkeyBallCourseSection
{
    public MonkeyBallCourseSection()
    {
    }

    public MonkeyBallCourseSection(float minimumZ, float maximumZ, float halfWidth)
    {
        MinimumZ = minimumZ;
        MaximumZ = maximumZ;
        HalfWidth = halfWidth;
    }

    public float MinimumZ { get; set; }

    public float MaximumZ { get; set; }

    public float HalfWidth { get; set; }
}
