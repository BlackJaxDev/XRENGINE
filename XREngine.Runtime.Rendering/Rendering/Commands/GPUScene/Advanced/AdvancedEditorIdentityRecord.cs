using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// GPU-visible stable identity used by picking and editor diagnostics.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedEditorIdentityRecord
{
    public ulong StableInstanceId;
    public ulong IdentityLow;
    public ulong IdentityHigh;
    public uint SelectionId;
    public uint Flags;
}
