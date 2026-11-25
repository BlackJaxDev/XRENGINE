using System.Runtime.InteropServices;

namespace XREngine.Rendering.UI;

/// <summary>ABI of IDWriteTextAnalysisSource in Windows SDK dwrite.h.</summary>
[ComVisible(true), Guid("688e1a58-5094-47c8-adc8-fbcea60ae92b"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDWriteTextAnalysisSourceNative
{
    [PreserveSig] int GetTextAtPosition(uint position, out nint text, out uint length);
    [PreserveSig] int GetTextBeforePosition(uint position, out nint text, out uint length);
    [PreserveSig] int GetParagraphReadingDirection();
    [PreserveSig] int GetLocaleName(uint position, out uint length, out nint locale);
    [PreserveSig] int GetNumberSubstitution(uint position, out uint length, out nint substitution);
}
