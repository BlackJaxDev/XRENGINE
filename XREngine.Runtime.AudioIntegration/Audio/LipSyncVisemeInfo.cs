namespace XREngine.Components;

/// <summary>Names and order of the visemes supplied by the lip-sync backend.</summary>
public static class LipSyncVisemeInfo
{
    public static readonly string[] Names =
    [
        "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou"
    ];

    public const int Count = 15;
}
