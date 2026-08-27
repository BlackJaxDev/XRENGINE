namespace XREngine.Animation
{
    /// <summary>
    /// Behavior outside the authored key range. Numeric values for Unity-backed
    /// modes match Unity's WrapMode where that API exposes distinct values.
    /// </summary>
    public enum EKeyframeInfinityMode
    {
        Default = 0,
        Once = 1,
        Loop = 2,
        PingPong = 4,
        ClampForever = 8,
        Clamp = 9,
    }
}
