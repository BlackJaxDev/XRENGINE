using System.Runtime.InteropServices;

namespace XREngine.Rendering.UI;

/// <summary>ABI of IDWriteTextAnalysisSink in Windows SDK dwrite.h.</summary>
[ComVisible(true), Guid("5810cd44-0ca0-4701-b3fa-bec5182ae4f6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDWriteTextAnalysisSinkNative
{
    [PreserveSig] int SetScriptAnalysis(uint position, uint length, nint analysis);
    [PreserveSig] int SetLineBreakpoints(uint position, uint length, nint breakpoints);
    [PreserveSig] int SetBidiLevel(uint position, uint length, byte explicitLevel, byte resolvedLevel);
    [PreserveSig] int SetNumberSubstitution(uint position, uint length, nint substitution);
}
