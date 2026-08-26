using XREngine.Scene;

namespace XREngine;

/// <summary>
/// Bootstrap-owned operations for composing and operating Core worlds. The
/// Engine facade depends on this contract instead of a Bootstrap implementation.
/// </summary>
public interface IRuntimeWorldHostServices
{
    RuntimeWorld GetOrCreate(XRWorld world);
    void Retarget(RuntimeWorld world, XRWorld targetWorld);
    bool Remove(XRWorld world, bool dispose = true);
    Task BeginPlayAsync(RuntimeWorld world);
    Task BeginEditModeAsync(RuntimeWorld world);
    void EndPlay(RuntimeWorld world);
}
