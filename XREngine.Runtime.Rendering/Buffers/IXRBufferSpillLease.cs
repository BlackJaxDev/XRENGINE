namespace XREngine.Rendering;

/// <summary>Owns one acquired pointer and its host mapping resources.</summary>
public interface IXRBufferSpillLease
{
    IntPtr Address { get; }
    uint Length { get; }

    /// <summary>Releases the pointer once. Explicit disposal also closes the mapping.</summary>
    void Release(bool disposing);
}
