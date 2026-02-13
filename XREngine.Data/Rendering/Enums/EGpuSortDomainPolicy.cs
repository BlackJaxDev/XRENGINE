namespace XREngine.Data.Rendering
{
    public enum EGpuSortDomain
    {
        MaterialStateGrouping = 0,
        OpaqueFrontToBack = 1,
        TransparentBackToFront = 2,
    }

    public enum EGpuSortDomainPolicy
    {
        MaterialStateGrouping = 0,
        OpaqueFrontToBackAndMaterial = 1,
        OpaqueFrontToBackTransparentBackToFront = 2,
    }
}