using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>Authored controls associated with one canonical aggregate job.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public readonly record struct AdvancedDeformationJobControls(uint InfluenceCap, float MorphWeightThreshold);
