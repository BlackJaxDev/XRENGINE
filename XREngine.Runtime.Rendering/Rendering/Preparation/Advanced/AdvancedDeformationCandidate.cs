using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Admission metadata kept outside the GPU job record.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedDeformationCandidate(
    AdvancedDeformationJobRecord Job,
    AdvancedDeformationJobKey Key,
    float ProjectedContribution,
    bool Mandatory,
    bool Visible);
