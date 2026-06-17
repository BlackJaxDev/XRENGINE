namespace XREngine;

internal sealed class RuntimeOpenVrApi
{
    public bool IsHeadsetPresent => CVR is not null;
    public CVRSystem? CVR { get; set; }
}
