using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Identifies one native dock window for the life of a pointer drag.</summary>
internal readonly record struct NativeDockDragWindowAdmission(
    NativeDockWindowRegistration Registration,
    ulong RegistrationRevision,
    ObjectConstructionIdentity RegistrationIdentity,
    ObjectConstructionIdentity RootIdentity,
    XRWindow Window,
    IRuntimeWindowBackend Backend,
    long BackendLifetime,
    nint WindowHandle,
    string Key,
    object Context)
{
    internal bool IsCurrent =>
        RegistrationIdentity.IsCurrent && RootIdentity.IsCurrent &&
        !Registration.IsDestroyed && !Registration.IsDestroyQueued &&
        RootIdentity.Target is SceneNode { IsDestroyed: false, IsDestroyQueued: false, IsActiveInHierarchy: true } root &&
        ReferenceEquals(root, Registration.SceneNode) &&
        ReferenceEquals(Window.DesktopWindowBackend, Backend) &&
        !Backend.IsClosing &&
        !Backend.Events.IsClosingOrDisposed &&
        Backend.LifetimeGeneration == BackendLifetime &&
        Backend.OperatingSystemWindowHandle == WindowHandle &&
        WindowHandle != 0 &&
        Registration.RegistrationRevision == RegistrationRevision &&
        Registration.TryGetRegistration(out string key, out object? context) &&
        key == Key && ReferenceEquals(context, Context) &&
        NativeDockWindowRegistry.IsCurrentHost(Key, Registration) && HasCurrentPointerSource;

    private bool HasCurrentPointerSource
    {
        get
        {
            if (Backend is not IRuntimeDesktopDragPointerSource source)
                return false;
            RuntimeDesktopDragPointerSnapshot snapshot = source.ReadDesktopDragPointer();
            return snapshot.BackendLifetime == BackendLifetime &&
                snapshot.SourceWindowHandle == WindowHandle;
        }
    }
}
