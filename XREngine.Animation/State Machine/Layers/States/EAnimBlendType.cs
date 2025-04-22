namespace XREngine.Animation
{
    public enum EAnimBlendType
    {
        Linear,             //start + (end - start) * time
        CosineEaseInOut,    //start + (end - start) * (1.0f - cos(time)) / 2.0f
        QuadraticEaseStart, //start + (end - start) * time^power
        QuadraticEaseEnd,   //start + (end - start) * (1.0f - (1.0f - time)^power)
        Custom,             //start + (end - start) * customInterp(time)
    }
}
