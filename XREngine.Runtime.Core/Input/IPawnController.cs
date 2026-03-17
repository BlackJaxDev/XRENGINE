using XREngine.Components;
using XREngine.Networking;
using XREngine.Players;

namespace XREngine.Input
{
    /// <summary>
    /// Runtime-layer contract for pawn controllers.
    /// <para>
    /// PawnComponent stores its controller as <see cref="IPawnController"/> so that the
    /// concrete controller hierarchy (PawnController, LocalPlayerController, etc.) can live
    /// in a higher assembly (Runtime.InputIntegration) without creating a project cycle.
    /// </para>
    /// </summary>
    public interface IPawnController
    {
        /// <summary>
        /// Whether this controller represents a local player (as opposed to a remote/AI/server player).
        /// </summary>
        bool IsLocal { get; }

        /// <summary>
        /// Player identity information. Null for non-player controllers (e.g. AI).
        /// </summary>
        PlayerInfo? PlayerInfo { get; }

        /// <summary>
        /// Called per-frame by the pawn to dispatch input ticking.
        /// Implementations should no-op for non-local or input-less controllers.
        /// </summary>
        /// <param name="delta">Frame delta time in seconds.</param>
        /// <param name="isUIInputCaptured">Whether UI has captured input this frame.</param>
        void TickPawnInput(float delta, bool isUIInputCaptured);

        /// <summary>
        /// Notifies the controller that the controlled pawn's camera component changed.
        /// Local controllers typically rebind the viewport camera in response.
        /// </summary>
        void OnPawnCameraChanged();

        /// <summary>
        /// Exposes the controller's input device provider (e.g. <c>LocalInputInterface</c>).
        /// Null for non-local or input-less controllers. Consumers cast to the concrete
        /// input type at the consuming layer to avoid pulling input types into Runtime.Core.
        /// </summary>
        object? InputDevice { get; }

        /// <summary>
        /// The viewport associated with this controller (e.g. for split-screen rendering).
        /// Typed as <c>object?</c> to avoid pulling rendering types into Runtime.Core.
        /// Consumers in higher layers cast to <c>XRViewport</c>.
        /// </summary>
        object? Viewport { get; set; }

        /// <summary>
        /// The UI interactable that currently has focus for this controller.
        /// Typed as <c>object?</c> to avoid pulling UI types into Runtime.Core.
        /// </summary>
        object? FocusedInteractable { get; set; }

        /// <summary>
        /// The controlled pawn, exposed via the base <see cref="XRComponent"/> type so
        /// callers in lower layers can access it without depending on concrete pawn types.
        /// </summary>
        XRComponent? ControlledPawnComponent { get; set; }

        /// <summary>
        /// Queues the given pawn for possession.
        /// If the controller has no current pawn, possesses immediately.
        /// </summary>
        void EnqueuePossession(XRComponent pawn);

        /// <summary>
        /// The local player index for this controller, if applicable (null for AI / remote).
        /// </summary>
        ELocalPlayerIndex? LocalPlayerIndex { get; }

        /// <summary>
        /// Applies a network transform update to the controlled pawn.
        /// No-op for controllers that don't receive network state.
        /// </summary>
        void ApplyNetworkTransform(PlayerTransformUpdate update);
    }
}
