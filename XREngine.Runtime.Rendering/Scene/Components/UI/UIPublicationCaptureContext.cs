using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.Components;

/// <summary>
/// Inherited values that a publication capture walk passes from a node to its children.
/// The canvas keeps the value each node received, so a changed subtree can be captured
/// again without a walk from the root.
/// </summary>
internal readonly record struct UIPublicationCaptureContext(
    UIElementIdentity FocusScopeIdentity,
    UIElementIdentity VirtualListIdentity,
    UIElementIdentity VirtualRowIdentity,
    UIVirtualRowLease VirtualRowLease,
    ulong VirtualProviderIncarnation,
    UICompositionOwner CompositionOwner);
