namespace XREngine.Rendering;

/// <summary>One opaque canvas pass with a finite color clear and standard depth clear.</summary>
public readonly struct BrowserRenderPassDescription
{
    public BrowserRenderPassDescription(float clearRed, float clearGreen, float clearBlue, float clearAlpha, float clearDepth)
    {
        ClearRed = clearRed;
        ClearGreen = clearGreen;
        ClearBlue = clearBlue;
        ClearAlpha = clearAlpha;
        ClearDepth = clearDepth;
        Validate();
    }

    public float ClearRed { get; }
    public float ClearGreen { get; }
    public float ClearBlue { get; }
    public float ClearAlpha { get; }
    public float ClearDepth { get; }

    /// <summary>Rejects default or malformed descriptions before a packet can be written.</summary>
    public void Validate()
    {
        if (!InUnitRange(ClearRed) || !InUnitRange(ClearGreen) || !InUnitRange(ClearBlue)
            || ClearAlpha != 1f || ClearDepth != 1f)
            throw new ArgumentOutOfRangeException(nameof(BrowserRenderPassDescription),
                "The canvas pass requires finite RGB components in [0, 1], opaque alpha, and a depth clear of 1.");
    }

    private static bool InUnitRange(float value) => float.IsFinite(value) && value >= 0f && value <= 1f;
}
