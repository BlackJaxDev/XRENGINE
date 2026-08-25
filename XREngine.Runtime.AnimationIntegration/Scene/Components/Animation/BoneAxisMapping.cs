namespace XREngine.Components.Animation;

/// <summary>
/// Defines how a bone's local axes map to humanoid twist/front-back/left-right rotations.
/// </summary>
public struct BoneAxisMapping
{
    public int TwistAxis { get; set; }
    public int TwistSign { get; set; }
    public int FrontBackAxis { get; set; }
    public int FrontBackSign { get; set; }
    public int LeftRightAxis { get; set; }
    public int LeftRightSign { get; set; }

    public static BoneAxisMapping Default => new()
    {
        TwistAxis = 1,
        TwistSign = 1,
        FrontBackAxis = 0,
        FrontBackSign = 1,
        LeftRightAxis = 2,
        LeftRightSign = 1,
    };
}
