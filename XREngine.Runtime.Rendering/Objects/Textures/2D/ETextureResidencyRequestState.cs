namespace XREngine.Rendering;

/// <summary>The state of an explicit texture residency request.</summary>
public enum ETextureResidencyRequestState
{
    /// <summary>The ticket identifies no active request. The request was cancelled, or it never existed.</summary>
    None = 0,

    /// <summary>The requested mip is not published yet. A request that was resident goes back to this state if the texture demotes.</summary>
    Pending = 1,

    /// <summary>The requested mip, after the texture quality cap, is published and the GPU can sample it.</summary>
    Resident = 2,
}
